#pragma once

//
// Wacom One by Wacom (CTL-472) Hardware Specifications & USB Protocol
//

#define WACOM_VENDOR_ID                 0x056A
#define WACOM_CTL472_PRODUCT_ID         0x037A

//
// Physical & Digitizer Specifications
//
#define CTL472_PHYSICAL_WIDTH_MM        152.0f
#define CTL472_PHYSICAL_HEIGHT_MM       95.0f

#define CTL472_MAX_X                    15200   // 100 counts/mm (2540 LPI)
#define CTL472_MAX_Y                    9500    // 100 counts/mm (2540 LPI)
#define CTL472_MAX_PRESSURE             2047    // 11-bit pressure resolution (0..2047)

#define OSUKERNEL_DEFAULT_MAX_X         CTL472_MAX_X
#define OSUKERNEL_DEFAULT_MAX_Y         CTL472_MAX_Y
#define OSUKERNEL_DEFAULT_MAX_PRESSURE  CTL472_MAX_PRESSURE

//
// USB Endpoint & Packet Parameters
//
#define CTL472_USB_INTERRUPT_IN_PIPE    0x81
#define CTL472_REPORT_LENGTH_RAW        10      // 10 bytes without prepended driver byte
#define CTL472_REPORT_LENGTH_PREPENDED  11      // 11 bytes when driver prepends Report ID

#define OSUKERNEL_USB_INTERRUPT_IN_PIPE    CTL472_USB_INTERRUPT_IN_PIPE
#define OSUKERNEL_REPORT_LENGTH_RAW        CTL472_REPORT_LENGTH_RAW
#define OSUKERNEL_REPORT_LENGTH_PREPENDED  CTL472_REPORT_LENGTH_PREPENDED

//
// Feature Report to Initialize Tablet into Absolute Digitizer Mode
//
#define CTL472_INIT_FEATURE_REPORT_ID   0x02
#define CTL472_INIT_FEATURE_REPORT_VAL  0x02

#define OSUKERNEL_INIT_FEATURE_REPORT_ID   CTL472_INIT_FEATURE_REPORT_ID
#define OSUKERNEL_INIT_FEATURE_REPORT_VAL  CTL472_INIT_FEATURE_REPORT_VAL

//
// Packet Format (Raw 10-byte Intuos V2 Packet):
//
// Byte 0: Report ID (0x02 = Pen Tool Report)
// Byte 1: Status & Button Flags:
//   Bit 0 (0x01): Tip Switch (1 = Tip contacting surface, 0 = Hovering)
//   Bit 1 (0x02): Pen Button 1 (Lower barrel button)
//   Bit 2 (0x04): Pen Button 2 (Upper barrel button)
//   Bit 3 (0x08): Eraser flag
//   Bit 6 (0x40): Proximity Bit (1 = Tool in active sensing range)
//   Bit 7 (0x80): Out of Range Flag (0x80 = Pen just left sensing area)
//
// Bytes 2-3: X Coordinate (16-bit Little-Endian, 0..15200)
// Bytes 4-5: Y Coordinate (16-bit Little-Endian, 0..9500)
// Bytes 6-7: Pressure (16-bit Little-Endian, 0..2047)
// Byte 8: Hover Distance (0..255)
// Byte 9: Reserved / Checksum
//

#define WACOM_REPORT_ID_PEN             0x02
#define WACOM_REPORT_ID_EXTENDED        0x10

#define WACOM_STATUS_TIP_SWITCH         0x01
#define WACOM_STATUS_BARREL_BTN1        0x02
#define WACOM_STATUS_BARREL_BTN2        0x04
#define WACOM_STATUS_ERASER             0x08
#define WACOM_STATUS_PROXIMITY          0x40
#define WACOM_STATUS_OUT_OF_RANGE       0x80

#pragma pack(push, 1)

typedef struct _WACOM_CTL472_RAW_PACKET {
    UCHAR  ReportId;       // 0x02
    UCHAR  Status;         // Flags: Proximity, Tip, Buttons
    USHORT X;              // Little-endian X (0..15200)
    USHORT Y;              // Little-endian Y (0..9500)
    USHORT Pressure;       // Little-endian Pressure (0..2047)
    UCHAR  HoverDistance;  // Distance from surface
    UCHAR  Padding;
} WACOM_CTL472_RAW_PACKET, *PWACOM_CTL472_RAW_PACKET;

#pragma pack(pop)
