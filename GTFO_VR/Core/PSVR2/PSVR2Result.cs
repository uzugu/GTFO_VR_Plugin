namespace GTFO_VR.Core.PSVR2
{
    /// <summary>
    /// Result of a PSVR2 Toolkit call. The negative CAPI codes mirror PSVR2TK_RESULT_* in
    /// psvr2tk_capi.h (Toolkit v1.0.0 experimental); the others come from our transports.
    /// </summary>
    internal enum PSVR2Result
    {
        Ok = 0,
        DriverInactive = -1,
        NoSlot = -2,
        Timeout = -3,
        InvalidParameter = -4,

        NotConnected = -100,
        Unsupported = -101,
        Failed = -102
    }

    internal static class PSVR2ResultCodes
    {
        internal static PSVR2Result FromCapi(int code)
        {
            switch (code)
            {
                case 0:
                    return PSVR2Result.Ok;
                case -1:
                    return PSVR2Result.DriverInactive;
                case -2:
                    return PSVR2Result.NoSlot;
                case -3:
                    return PSVR2Result.Timeout;
                case -4:
                    return PSVR2Result.InvalidParameter;
                default:
                    return code > 0 ? PSVR2Result.Ok : PSVR2Result.Failed;
            }
        }
    }
}
