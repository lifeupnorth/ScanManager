namespace ScanManager.Wia;

/// <summary>Property ids, flags and error codes from wiadef.h / the WIA Automation Library.</summary>
internal static class WiaConstants
{
    public const int ScannerDeviceType = 1;

    public const string FormatBmp = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";

    // DeviceInfo / root item properties
    public const int DipDevName = 7;
    public const int DipDevDesc = 4;
    public const int DpsHorizontalBedSize = 3074;
    public const int DpsVerticalBedSize = 3075;
    public const int DpsHorizontalSheetFeedSize = 3076;
    public const int DpsVerticalSheetFeedSize = 3077;
    public const int DpsSheetFeederRegistration = 3078;
    public const int DpsDocumentHandlingCapabilities = 3086;
    public const int DpsDocumentHandlingSelect = 3088;
    public const int DpsPages = 3096;
    public const int DpsPreview = 3100;

    // Scan item properties
    public const int IpaDataType = 4103;
    public const int IpaDepth = 4104;
    public const int IpsCurIntent = 6146;
    public const int IpsXRes = 6147;
    public const int IpsYRes = 6148;
    public const int IpsXPos = 6149;
    public const int IpsYPos = 6150;
    public const int IpsXExtent = 6151;
    public const int IpsYExtent = 6152;
    public const int IpsBrightness = 6154;
    public const int IpsContrast = 6155;

    // WIA_DPS_DOCUMENT_HANDLING_* flags
    public const int Feeder = 0x001;
    public const int Flatbed = 0x002;
    public const int Duplex = 0x004;

    // WIA_DPS_SHEET_FEEDER_REGISTRATION
    public const int Centered = 1;
    public const int RightJustified = 2;

    // WIA_IPA_DATATYPE
    public const int DataTypeThreshold = 0;
    public const int DataTypeGrayscale = 2;
    public const int DataTypeColor = 3;

    // WIA_IPS_CUR_INTENT
    public const int IntentColor = 0x1;
    public const int IntentGrayscale = 0x2;
    public const int IntentText = 0x4;

    // WIA_PROP_* subtypes as exposed by WIA.Property.SubType
    public const int SubTypeRange = 1;
    public const int SubTypeList = 2;

    // HRESULTs
    public const int ErrorPaperJam = unchecked((int)0x80210002);
    public const int ErrorPaperEmpty = unchecked((int)0x80210003);
    public const int ErrorPaperProblem = unchecked((int)0x80210004);
    public const int ErrorOffline = unchecked((int)0x80210005);
    public const int ErrorBusy = unchecked((int)0x80210006);
    public const int ErrorWarmingUp = unchecked((int)0x80210007);
    public const int ErrorUserIntervention = unchecked((int)0x80210008);
    public const int ErrorItemDeleted = unchecked((int)0x80210009);
    public const int ErrorDeviceCommunication = unchecked((int)0x8021000A);
    public const int ErrorInvalidCommand = unchecked((int)0x8021000B);
    public const int ErrorIncorrectHardwareSetting = unchecked((int)0x8021000C);
    public const int ErrorDeviceLocked = unchecked((int)0x8021000D);
    public const int ErrorNoDeviceAvailable = unchecked((int)0x80210015);
    public const int ErrorCoverOpen = unchecked((int)0x80210016);
    public const int ErrorLampOff = unchecked((int)0x80210017);
}
