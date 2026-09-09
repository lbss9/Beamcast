using Windows.Networking.Connectivity;

namespace Beamcast;

/// <summary>
/// Whether the connection in use charges for data (a phone hotspot, a mobile modem, a plan over its
/// limit). Background downloads stay off on those, the way Windows and PowerToys do it. Anything
/// the API cannot answer counts as not metered, so a plain desktop never stops updating by mistake.
/// </summary>
public static class NetworkCost
{
    public static bool IsMetered()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            var cost = profile?.GetConnectionCost();
            if (cost is null)
                return false;
            return cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable
                || cost.OverDataLimit
                || cost.Roaming;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
