using Tide.Domain.Enums;
using Tide.Domain.Observables;

namespace Tide.Application.Indicators;

/// <summary>How an indicator is named and shown to people: its type's label, and its value defanged where it could be
/// clicked or auto-linked.</summary>
public static class IndicatorLabels
{
    public static string For(IndicatorType t) => t switch
    {
        IndicatorType.IpAddress => "IP address",
        IndicatorType.Url => "URL",
        IndicatorType.FileHash => "File hash",
        IndicatorType.FileName => "File name",
        IndicatorType.EmailAddress => "Email",
        IndicatorType.RegistryKey => "Registry key",
        IndicatorType.CommandLine => "Command line",
        IndicatorType.CryptoWallet => "Crypto wallet",
        _ => t.ToString(),
    };

    public static string Shown(IndicatorType t, string value) => IocObservable.Defang(t, value);
}
