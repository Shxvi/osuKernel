#include <initguid.h>
#include "osuKernel.h"

//
// Driver Entry Point
//
extern "C"
NTSTATUS DriverEntry(
    _In_ PDRIVER_OBJECT  DriverObject,
    _In_ PUNICODE_STRING RegistryPath
)
{
    NTSTATUS status;
    WDF_DRIVER_CONFIG config;
    WDF_OBJECT_ATTRIBUTES attributes;

    WDF_DRIVER_CONFIG_INIT(&config, OsuKernelEvtDeviceAdd);
    config.EvtDriverUnload = OsuKernelEvtDriverUnload;

    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);

    status = WdfDriverCreate(
        DriverObject,
        RegistryPath,
        &attributes,
        &config,
        WDF_NO_HANDLE
    );

    if (!NT_SUCCESS(status)) {
        KdPrintDbg("WdfDriverCreate failed: 0x%08X\n", status);
        return status;
    }

    KdPrintDbg("osuKernel Ultra-Low Latency Driver loaded.\n");
    return STATUS_SUCCESS;
}

//
// Driver Unload Routine
//
VOID OsuKernelEvtDriverUnload(
    _In_ WDFDRIVER Driver
)
{
    UNREFERENCED_PARAMETER(Driver);
    PAGED_CODE();
    KdPrintDbg("osuKernel Driver unloaded.\n");
}
