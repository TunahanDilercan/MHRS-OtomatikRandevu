using System;

namespace MHRS_OtomatikRandevu.Exceptions
{
    /// <summary>
    /// Exception thrown when MHRS session expires (LGN2001 error)
    /// </summary>
    public class SessionExpiredException : Exception
    {
        public SessionExpiredException() : base()
        {
        }

        public SessionExpiredException(string message) : base(message)
        {
        }

        public SessionExpiredException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
