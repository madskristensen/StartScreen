namespace StartScreen.Services.DevHub
{
    internal sealed class DashboardPublicationWindow
    {
        private readonly Func<DateTimeOffset> _utcNow;
        private DateTimeOffset _nextPublication;
        internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

        internal DashboardPublicationWindow(Func<DateTimeOffset> utcNow = null)
        {
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        }

        internal TimeSpan Delay => _nextPublication > _utcNow() ? _nextPublication - _utcNow() : TimeSpan.Zero;

        internal bool ShouldPublish(bool force = false)
        {
            if (!force && _utcNow() < _nextPublication)
                return false;
            _nextPublication = _utcNow().Add(Interval);
            return true;
        }
    }
}
