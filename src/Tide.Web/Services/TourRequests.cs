namespace Tide.Web.Services;

public sealed class TourRequests
{
    public event Action? Asked;

    public void Ask() => Asked?.Invoke();
}
