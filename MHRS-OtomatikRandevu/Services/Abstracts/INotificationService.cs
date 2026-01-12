namespace MHRS_OtomatikRandevu.Services.Abstracts
{
    public interface INotificationService : IDisposable
    {
        public Task SendNotification(string message);
    }
}