#include "osuKernel.h"

// 360-degree 16.16 fixed-point sine table (sin(0 deg) = 0, sin(90 deg) = 65536)
static const LONG g_SinFixed16[360] = {
#include "sintable.inc"
};

//
// Precomputes 32.16 fixed point scale multipliers and 16.16 rotation trigonometry
// whenever the active area or rotation changes.
// Called under DevCtx->ConfigLock.
//
VOID OsuKernelRecalculateScales(
    _Inout_ PDEVICE_CONTEXT DevCtx
)
{
    ULONG w = DevCtx->AreaConfig.TabletInputWidth;
    ULONG h = DevCtx->AreaConfig.TabletInputHeight;

    if (w > 0 && h > 0) {
        // (ScreenOutputWidth << 16) / TabletInputWidth
        DevCtx->ScaleFixedX = ((ULONG64)DevCtx->AreaConfig.ScreenOutputWidth << 16) / w;

        // (ScreenOutputHeight << 16) / TabletInputHeight
        DevCtx->ScaleFixedY = ((ULONG64)DevCtx->AreaConfig.ScreenOutputHeight << 16) / h;
    } else {
        DevCtx->ScaleFixedX = 0x10000; // 1.0 in 16.16
        DevCtx->ScaleFixedY = 0x10000;
    }

    // Precalculate geometric centers and half-dimensions for lockless DPC execution
    DevCtx->HalfW = (LONG)(w / 2);
    DevCtx->HalfH = (LONG)(h / 2);
    DevCtx->CenterX = DevCtx->AreaConfig.TabletInputX + DevCtx->HalfW;
    DevCtx->CenterY = DevCtx->AreaConfig.TabletInputY + DevCtx->HalfH;

    // Precompute 16.16 sin and cos for the configured rotation angle (normalized to 0 - 359 degrees)
    ULONG angle = ((DevCtx->AreaConfig.RotationAngle % 360) + 360) % 360;
    DevCtx->SinAngleFixed = g_SinFixed16[angle];
    DevCtx->CosAngleFixed = g_SinFixed16[(angle + 90) % 360];

    // Precalculate squared deadzone to avoid square root when stationary
    LONGLONG dz = (LONGLONG)DevCtx->Settings.AntichatterDeadzone;
    DevCtx->DeadzoneSq = dz * dz;
}

//
// Ultra-Low Latency Kernel Coordinate Transformation
// Runs at IRQL <= DISPATCH_LEVEL in the USB Interrupt DPC.
// Lock-free execution: reads 32/64-bit aligned configuration fields without spinlock overhead.
// Zero floating-point instructions, zero memory allocations, pure 64-bit integer registers.
//
// Returns TRUE if point should be processed and dispatched, FALSE if limited/dropped.
//
BOOLEAN OsuKernelTransformCoordinates(
    _In_ PDEVICE_CONTEXT DevCtx,
    _In_ USHORT RawX,
    _In_ USHORT RawY,
    _Out_ PLONG OutScreenX,
    _Out_ PLONG OutScreenY
)
{
    // Fast path: if RawPassthrough is enabled, output raw coordinates directly
    if (DevCtx->Settings.RawPassthrough) {
        *OutScreenX = (LONG)RawX;
        *OutScreenY = (LONG)RawY;
        return TRUE;
    }

    // Lock-free read of 32/64-bit aligned configuration (atomic on x64)
    ULONG inputW = DevCtx->AreaConfig.TabletInputWidth;
    ULONG inputH = DevCtx->AreaConfig.TabletInputHeight;

    if (inputW == 0 || inputH == 0) {
        *OutScreenX = (LONG)RawX;
        *OutScreenY = (LONG)RawY;
        return TRUE;
    }

    LONG inputX = DevCtx->AreaConfig.TabletInputX;
    LONG inputY = DevCtx->AreaConfig.TabletInputY;
    ULONG angle = DevCtx->AreaConfig.RotationAngle;
    ULONG clipping = DevCtx->AreaConfig.AreaClipping;
    ULONG limiting = DevCtx->AreaConfig.AreaLimiting;
    LONG screenOutX = DevCtx->AreaConfig.ScreenOutputX;
    LONG screenOutY = DevCtx->AreaConfig.ScreenOutputY;
    ULONG64 scaleX = DevCtx->ScaleFixedX;
    ULONG64 scaleY = DevCtx->ScaleFixedY;
    LONGLONG cosVal = DevCtx->CosAngleFixed;
    LONGLONG sinVal = DevCtx->SinAngleFixed;
    LONG cx = DevCtx->CenterX;
    LONG cy = DevCtx->CenterY;
    LONG halfW = DevCtx->HalfW;
    LONG halfH = DevCtx->HalfH;

    LONG dx = (LONG)RawX - cx;
    LONG dy = (LONG)RawY - cy;

    LONG relX = 0;
    LONG relY = 0;

    // Apply rotation around center of active area (0 to 360 degrees)
    if (angle % 360 == 0) {
        relX = (LONG)RawX - inputX;
        relY = (LONG)RawY - inputY;
    } else {
        // 2D Rotation:
        // rotDx = dx * cos(theta) + dy * sin(theta)
        // rotDy = -dx * sin(theta) + dy * cos(theta)
        LONGLONG rotDx = (dx * cosVal + dy * sinVal) >> 16;
        LONGLONG rotDy = (-dx * sinVal + dy * cosVal) >> 16;

        relX = halfW + (LONG)rotDx;
        relY = halfH + (LONG)rotDy;
    }

    // Check AreaLimiting (ignore inputs outside the designated tablet box)
    if (limiting) {
        if (relX < 0 || relX > (LONG)inputW ||
            relY < 0 || relY > (LONG)inputH) {
            return FALSE; // Drop packet
        }
    }

    // Check AreaClipping (clamp to active area boundary)
    if (clipping) {
        if (relX < 0) relX = 0;
        if (relX > (LONG)inputW) relX = (LONG)inputW;
        if (relY < 0) relY = 0;
        if (relY > (LONG)inputH) relY = (LONG)inputH;
    }

    // Scale from tablet space to screen space using precomputed 16.16 fixed point:
    // ScreenX = ScreenOutputX + (relX * ScaleFixedX >> 16)
    LONG scaledX = (LONG)(((LONG64)relX * scaleX) >> 16);
    LONG scaledY = (LONG)(((LONG64)relY * scaleY) >> 16);

    LONG finalX = screenOutX + scaledX;
    LONG finalY = screenOutY + scaledY;

    *OutScreenX = finalX;
    *OutScreenY = finalY;

    return TRUE;
}
