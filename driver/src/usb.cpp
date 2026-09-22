#include "osuKernel.h"

//
// Configures USB target device, finds the interrupt IN pipe, and sets up continuous reader.
//
NTSTATUS OsuKernelConfigureUsb(
    _In_ WDFDEVICE Device,
    _In_ PDEVICE_CONTEXT DevCtx
)
{
    NTSTATUS status;
    WDF_USB_DEVICE_SELECT_CONFIG_PARAMS configParams;

    // Create USB target device if not already created
    if (DevCtx->UsbDevice == NULL) {
        status = WdfUsbTargetDeviceCreate(Device, WDF_NO_OBJECT_ATTRIBUTES, &DevCtx->UsbDevice);
        if (!NT_SUCCESS(status)) {
            KdPrintDbg("WdfUsbTargetDeviceCreate failed: 0x%08X\n", status);
            return status;
        }
    }

    // Select configuration (try single interface first for non-composite device, then multi-interface)
    WDF_USB_DEVICE_SELECT_CONFIG_PARAMS_INIT_SINGLE_INTERFACE(&configParams);
    status = WdfUsbTargetDeviceSelectConfig(DevCtx->UsbDevice, WDF_NO_OBJECT_ATTRIBUTES, &configParams);
    if (!NT_SUCCESS(status)) {
        KdPrintDbg("WdfUsbTargetDeviceSelectConfig single interface failed: 0x%08X, fallback to multi\n", status);
        WDF_USB_DEVICE_SELECT_CONFIG_PARAMS_INIT_MULTIPLE_INTERFACES(&configParams, 0, NULL);
        status = WdfUsbTargetDeviceSelectConfig(DevCtx->UsbDevice, WDF_NO_OBJECT_ATTRIBUTES, &configParams);
        if (!NT_SUCCESS(status)) {
            KdPrintDbg("WdfUsbTargetDeviceSelectConfig failed: 0x%08X\n", status);
            return status;
        }
    }

    // Locate the Interrupt IN pipe
    DevCtx->InterruptPipe = NULL;
    ULONG maxPacketSize = 0;

    if (configParams.Type == WdfUsbTargetDeviceSelectConfigTypeSingleInterface) {
        DevCtx->UsbInterface = configParams.Types.SingleInterface.ConfiguredUsbInterface;
        UCHAR numPipes = WdfUsbInterfaceGetNumConfiguredPipes(DevCtx->UsbInterface);
        for (UCHAR p = 0; p < numPipes; p++) {
            WDF_USB_PIPE_INFORMATION pipeInfo;
            WDF_USB_PIPE_INFORMATION_INIT(&pipeInfo);
            WDFUSBPIPE pipe = WdfUsbInterfaceGetConfiguredPipe(DevCtx->UsbInterface, p, &pipeInfo);
            if (pipeInfo.PipeType == WdfUsbPipeTypeInterrupt && WdfUsbTargetPipeIsInEndpoint(pipe)) {
                DevCtx->InterruptPipe = pipe;
                maxPacketSize = pipeInfo.MaximumPacketSize;
                KdPrintDbg("Found Interrupt IN pipe on Single Interface (Addr 0x%02X, MaxPacket %u)\n",
                    pipeInfo.EndpointAddress, maxPacketSize);
                break;
            }
        }
    }

    if (DevCtx->InterruptPipe == NULL) {
        UCHAR numInterfaces = WdfUsbTargetDeviceGetNumInterfaces(DevCtx->UsbDevice);
        for (UCHAR ifaceIdx = 0; ifaceIdx < numInterfaces; ifaceIdx++) {
            WDFUSBINTERFACE iface = WdfUsbTargetDeviceGetInterface(DevCtx->UsbDevice, ifaceIdx);
            UCHAR numPipes = WdfUsbInterfaceGetNumConfiguredPipes(iface);
            for (UCHAR p = 0; p < numPipes; p++) {
                WDF_USB_PIPE_INFORMATION pipeInfo;
                WDF_USB_PIPE_INFORMATION_INIT(&pipeInfo);
                WDFUSBPIPE pipe = WdfUsbInterfaceGetConfiguredPipe(iface, p, &pipeInfo);
                if (pipeInfo.PipeType == WdfUsbPipeTypeInterrupt && WdfUsbTargetPipeIsInEndpoint(pipe)) {
                    DevCtx->UsbInterface = iface;
                    DevCtx->InterruptPipe = pipe;
                    maxPacketSize = pipeInfo.MaximumPacketSize;
                    KdPrintDbg("Found Interrupt IN pipe on Interface %u, Pipe %u (Addr 0x%02X, MaxPacket %u)\n",
                        ifaceIdx, p, pipeInfo.EndpointAddress, maxPacketSize);
                    break;
                }
            }
            if (DevCtx->InterruptPipe != NULL) break;
        }
    }

    if (DevCtx->InterruptPipe == NULL) {
        KdPrintDbg("Failed to find Interrupt IN pipe!\n");
        return STATUS_DEVICE_CONFIGURATION_ERROR;
    }

    // Disable maximum packet size check so the framework does not return STATUS_INVALID_BUFFER_SIZE (Code 10)
    WdfUsbTargetPipeSetNoMaximumPacketSizeCheck(DevCtx->InterruptPipe);

    // Send the initialization feature report [0x02, 0x02] to switch CTL-472 into raw digitizer mode
    status = OsuKernelSendFeatureReport(DevCtx, OSUKERNEL_INIT_FEATURE_REPORT_ID, OSUKERNEL_INIT_FEATURE_REPORT_VAL);
    if (!NT_SUCCESS(status)) {
        KdPrintDbg("OsuKernelSendFeatureReport failed: 0x%08X (continuing)\n", status);
    }

    // Configure the continuous reader for lowest-latency interrupt streaming
    size_t transferLength = (maxPacketSize > 0) ? maxPacketSize : 64;
    if (transferLength < OSUKERNEL_REPORT_LENGTH_PREPENDED) {
        transferLength = OSUKERNEL_REPORT_LENGTH_PREPENDED;
    }

    WDF_USB_CONTINUOUS_READER_CONFIG readerConfig;
    WDF_USB_CONTINUOUS_READER_CONFIG_INIT(
        &readerConfig,
        OsuKernelEvtUsbReadComplete,
        DevCtx,
        transferLength
    );

    readerConfig.NumPendingReads = 2; // Double buffering keeps the USB host controller saturated
    readerConfig.EvtUsbTargetPipeReadersFailed = OsuKernelEvtUsbReadFailed;

    status = WdfUsbTargetPipeConfigContinuousReader(DevCtx->InterruptPipe, &readerConfig);
    if (!NT_SUCCESS(status)) {
        KdPrintDbg("WdfUsbTargetPipeConfigContinuousReader failed: 0x%08X\n", status);
        return status;
    }

    return STATUS_SUCCESS;
}

//
// Sends a USB HID Set_Report (Feature) control request to the device
// Standard Wacom feature packet: bmRequestType=0x21, bRequest=0x09, wValue=(0x0300 | ReportId), wIndex=Interface
//
NTSTATUS OsuKernelSendFeatureReport(
    _In_ PDEVICE_CONTEXT DevCtx,
    _In_ UCHAR ReportId,
    _In_ UCHAR ReportVal
)
{
    NTSTATUS status;
    WDF_USB_CONTROL_SETUP_PACKET setupPacket;
    WDF_MEMORY_DESCRIPTOR memDesc;
    UCHAR reportBuffer[2];

    if (DevCtx->UsbDevice == NULL || DevCtx->UsbInterface == NULL) {
        return STATUS_INVALID_DEVICE_STATE;
    }

    reportBuffer[0] = ReportId;
    reportBuffer[1] = ReportVal;

    // HID Set_Report: Class, Interface, Host-to-Device (0x21)
    // Request: 0x09 (SET_REPORT)
    // Value: (ReportType << 8) | ReportId -> Feature report is type 0x03 -> (0x03 << 8) | ReportId
    USHORT wValue = (USHORT)((0x03 << 8) | ReportId);
    UCHAR ifaceNum = WdfUsbInterfaceGetInterfaceNumber(DevCtx->UsbInterface);

    WDF_USB_CONTROL_SETUP_PACKET_INIT_CLASS(
        &setupPacket,
        BmRequestHostToDevice,
        BmRequestToInterface,
        0x09,       // SET_REPORT
        wValue,
        ifaceNum
    );

    // Explicitly set wLength to match buffer size to prevent STATUS_INFO_LENGTH_MISMATCH
    setupPacket.Packet.wLength = sizeof(reportBuffer);

    WDF_MEMORY_DESCRIPTOR_INIT_BUFFER(
        &memDesc,
        reportBuffer,
        sizeof(reportBuffer)
    );

    WDF_REQUEST_SEND_OPTIONS sendOptions;
    WDF_REQUEST_SEND_OPTIONS_INIT(&sendOptions, WDF_REQUEST_SEND_OPTION_SYNCHRONOUS);
    WDF_REQUEST_SEND_OPTIONS_SET_TIMEOUT(&sendOptions, WDF_REL_TIMEOUT_IN_MS(500));

    status = WdfUsbTargetDeviceSendControlTransferSynchronously(
        DevCtx->UsbDevice,
        NULL,
        &sendOptions,
        &setupPacket,
        &memDesc,
        NULL
    );

    return status;
}

//
// Callback executed at IRQL <= DISPATCH_LEVEL immediately when a USB packet arrives from the tablet
//
VOID OsuKernelEvtUsbReadComplete(
    _In_ WDFUSBPIPE Pipe,
    _In_ WDFMEMORY Buffer,
    _In_ size_t NumBytesTransferred,
    _In_ WDFCONTEXT Context
)
{
    UNREFERENCED_PARAMETER(Pipe);
    PDEVICE_CONTEXT devCtx = (PDEVICE_CONTEXT)Context;

    if (NumBytesTransferred < OSUKERNEL_REPORT_LENGTH_RAW) {
        return;
    }

    PUCHAR rawData = (PUCHAR)WdfMemoryGetBuffer(Buffer, NULL);
    if (rawData == NULL) {
        return;
    }

    OsuKernelProcessPacket(devCtx, rawData, NumBytesTransferred);
}

//
// Unpacks raw Wacom One CTL-472 packet, transforms coordinates, and updates stats
//
VOID OsuKernelProcessPacket(
    _In_ PDEVICE_CONTEXT DevCtx,
    _In_reads_bytes_(Length) PUCHAR Buffer,
    _In_ size_t Length
)
{
    // If the report begins with a driver prepended 0x00 or extra byte, adjust offset
    PUCHAR packet = Buffer;
    if (Length >= OSUKERNEL_REPORT_LENGTH_PREPENDED && packet[0] != WACOM_REPORT_ID_PEN && packet[1] == WACOM_REPORT_ID_PEN) {
        packet = Buffer + 1;
    }

    // Verify report ID (0x02 is tool report for Intuos / One)
    if (packet[0] != WACOM_REPORT_ID_PEN) {
        return;
    }

    UCHAR statusByte = packet[1];

    LARGE_INTEGER now = KeQueryPerformanceCounter(NULL);

    // Check Proximity bit (0x40) - tool in active sensing range
    BOOLEAN inProximity = (statusByte & WACOM_STATUS_PROXIMITY) != 0;
    if (!inProximity) {
        WdfSpinLockAcquire(DevCtx->StatsLock);
        DevCtx->Stats.InProximity = 0;
        DevCtx->Stats.LastButtons = 0;
        DevCtx->Stats.LastRawPressure = 0;
        DevCtx->Stats.TotalPackets++;
        DevCtx->Stats.LastPacketTimestamp = (ULONG64)now.QuadPart;
        DevCtx->FilterInitialized = FALSE;
        DevCtx->InterpDeltaTicks = 0;

        // Reset PPS if inactive for more than 400 ms
        LONGLONG idleTicks = now.QuadPart - DevCtx->LastPpsTimestamp.QuadPart;
        if (DevCtx->PerfFreq.QuadPart > 0 && idleTicks >= (DevCtx->PerfFreq.QuadPart * 4 / 10)) {
            DevCtx->Stats.PacketsPerSecond = 0;
            DevCtx->PpsCounter = 0;
            DevCtx->LastPpsTimestamp = now;
        }

        WdfSpinLockRelease(DevCtx->StatsLock);
        return;
    }

    // Unpack 16-bit Little-Endian fields
    USHORT rawX = (USHORT)(packet[2] | ((USHORT)packet[3] << 8));
    USHORT rawY = (USHORT)(packet[4] | ((USHORT)packet[5] << 8));

    // True raw pressure from sensor (16-bit word, standard CTL-472 range 0..2047)
    USHORT rawPressure = (USHORT)(packet[6] | ((USHORT)packet[7] << 8));

    // Antichatter deadzone filter (continuous mathematical deadband - zero step jumping)
    USHORT processX = rawX;
    USHORT processY = rawY;

    if (!DevCtx->FilterInitialized) {
        DevCtx->FilteredRawX16 = (LONGLONG)rawX << 16;
        DevCtx->FilteredRawY16 = (LONGLONG)rawY << 16;
        DevCtx->LastFilterRawX = rawX;
        DevCtx->LastFilterRawY = rawY;
        DevCtx->FilterInitialized = TRUE;
    } else {
        if (DevCtx->Settings.AntichatterDeadzone > 0) {
            LONG dx = (LONG)rawX - (LONG)DevCtx->LastFilterRawX;
            LONG dy = (LONG)rawY - (LONG)DevCtx->LastFilterRawY;
            LONGLONG distSq = (LONGLONG)dx * dx + (LONGLONG)dy * dy;

            if (distSq <= DevCtx->DeadzoneSq) {
                // Within stationary deadzone - keep stable position (zero jitter)
                processX = DevCtx->LastFilterRawX;
                processY = DevCtx->LastFilterRawY;
            } else {
                // Continuous smooth deadband transition (no discontinuous step jumps)
                LONG deadzone = (LONG)DevCtx->Settings.AntichatterDeadzone;
                ULONG dist = FastSqrt((ULONG)distSq);
                if (dist == 0) dist = 1;
                LONG excess = (LONG)dist - deadzone;
                if (excess < 0) excess = 0;

                DevCtx->LastFilterRawX = (USHORT)((LONG)DevCtx->LastFilterRawX + (dx * excess) / (LONG)dist);
                DevCtx->LastFilterRawY = (USHORT)((LONG)DevCtx->LastFilterRawY + (dy * excess) / (LONG)dist);
                processX = DevCtx->LastFilterRawX;
                processY = DevCtx->LastFilterRawY;
            }
        } else {
            DevCtx->LastFilterRawX = rawX;
            DevCtx->LastFilterRawY = rawY;
        }

        // Responsive smoothing filter (EMA) with 64-bit precision to prevent overflow
        if (DevCtx->Settings.EnableSmoothing > 0 && DevCtx->Settings.SmoothingStrength > 0) {
            ULONG strength = DevCtx->Settings.SmoothingStrength;
            if (strength > 95) strength = 95;
            ULONG keepWeight = 100 - strength;

            LONGLONG targetX16 = (LONGLONG)processX << 16;
            LONGLONG targetY16 = (LONGLONG)processY << 16;

            DevCtx->FilteredRawX16 = (DevCtx->FilteredRawX16 * (LONGLONG)strength + targetX16 * (LONGLONG)keepWeight) / 100;
            DevCtx->FilteredRawY16 = (DevCtx->FilteredRawY16 * (LONGLONG)strength + targetY16 * (LONGLONG)keepWeight) / 100;

            processX = (USHORT)(DevCtx->FilteredRawX16 >> 16);
            processY = (USHORT)(DevCtx->FilteredRawY16 >> 16);
        } else {
            DevCtx->FilteredRawX16 = (LONGLONG)processX << 16;
            DevCtx->FilteredRawY16 = (LONGLONG)processY << 16;
        }
    }

    // Digitizer tip activation & dynamic pressure mapping
    ULONG clickThreshold = DevCtx->Settings.TipClickThreshold;
    BOOLEAN tipClickDisabled = (DevCtx->Settings.DisableTipClick != 0);
    BOOLEAN tipDown = !tipClickDisabled && ((statusByte & WACOM_STATUS_TIP_SWITCH) != 0 || (clickThreshold > 0 && rawPressure >= clickThreshold));

    BOOLEAN pressureEnabled = (DevCtx->Settings.EnablePressure != 0);
    USHORT reportedPressure = pressureEnabled ? rawPressure : 0;

    UCHAR buttonFlags = 0;
    if (tipDown) buttonFlags |= 0x01;
    if (statusByte & WACOM_STATUS_BARREL_BTN1) buttonFlags |= 0x02;
    if (statusByte & WACOM_STATUS_BARREL_BTN2) buttonFlags |= 0x04;
    if (statusByte & WACOM_STATUS_ERASER)      buttonFlags |= 0x08;

    // Fast coordinate transformation in kernel mode
    LONG screenX = 0;
    LONG screenY = 0;
    BOOLEAN accepted = OsuKernelTransformCoordinates(DevCtx, processX, processY, &screenX, &screenY);

    if (!accepted) {
        WdfSpinLockAcquire(DevCtx->StatsLock);
        DevCtx->Stats.DroppedPackets++;
        DevCtx->Stats.LastRawX = rawX;
        DevCtx->Stats.LastRawY = rawY;
        DevCtx->Stats.LastRawPressure = reportedPressure;
        DevCtx->Stats.LastButtons = buttonFlags;
        DevCtx->Stats.InProximity = 1;
        DevCtx->Stats.LastPacketTimestamp = (ULONG64)now.QuadPart;
        WdfSpinLockRelease(DevCtx->StatsLock);
        return;
    }

    // Update real-time statistics & interpolation trajectory
    WdfSpinLockAcquire(DevCtx->StatsLock);
    DevCtx->Stats.TotalPackets++;
    DevCtx->PpsCounter++;

    // Calculate PPS every 125 ms (8 updates/sec) for rapid responsive feedback
    LONGLONG elapsedTicks = now.QuadPart - DevCtx->LastPpsTimestamp.QuadPart;
    LONGLONG windowTicks = (DevCtx->PerfFreq.QuadPart > 0) ? (DevCtx->PerfFreq.QuadPart / 8) : 1250000;
    if (elapsedTicks >= windowTicks && elapsedTicks > 0) {
        DevCtx->Stats.PacketsPerSecond = (ULONG)((DevCtx->PpsCounter * DevCtx->PerfFreq.QuadPart) / elapsedTicks);
        DevCtx->PpsCounter = 0;
        DevCtx->LastPpsTimestamp = now;
    }

    // Store trajectory for sub-packet interpolation up to 8 kHz
    DevCtx->InterpPrevScreenX = DevCtx->InterpCurrScreenX;
    DevCtx->InterpPrevScreenY = DevCtx->InterpCurrScreenY;
    DevCtx->InterpPrevTime = DevCtx->InterpCurrTime;
    DevCtx->InterpCurrScreenX = screenX;
    DevCtx->InterpCurrScreenY = screenY;
    DevCtx->InterpCurrTime = now;

    LONGLONG dt = now.QuadPart - DevCtx->InterpPrevTime.QuadPart;
    if (dt > 0) {
        DevCtx->InterpDeltaScreenX = DevCtx->InterpCurrScreenX - DevCtx->InterpPrevScreenX;
        DevCtx->InterpDeltaScreenY = DevCtx->InterpCurrScreenY - DevCtx->InterpPrevScreenY;
        DevCtx->InterpDeltaTicks = dt;
    }

    DevCtx->Stats.LastRawX = rawX;
    DevCtx->Stats.LastRawY = rawY;
    DevCtx->Stats.LastRawPressure = reportedPressure;
    DevCtx->Stats.LastButtons = buttonFlags;
    DevCtx->Stats.InProximity = 1;
    DevCtx->Stats.LastScreenX = screenX;
    DevCtx->Stats.LastScreenY = screenY;
    DevCtx->Stats.LastPacketTimestamp = (ULONG64)now.QuadPart;
    WdfSpinLockRelease(DevCtx->StatsLock);
}

//
// Continuous reader failure callback
//
BOOLEAN OsuKernelEvtUsbReadFailed(
    _In_ WDFUSBPIPE Pipe,
    _In_ NTSTATUS Status,
    _In_ USBD_STATUS UsbdStatus
)
{
    UNREFERENCED_PARAMETER(Pipe);
    UNREFERENCED_PARAMETER(Status);
    UNREFERENCED_PARAMETER(UsbdStatus);
    KdPrintDbg("Continuous reader failed: NTSTATUS=0x%08X, UsbdStatus=0x%08X\n", Status, UsbdStatus);
    return TRUE; // Continue reading
}
