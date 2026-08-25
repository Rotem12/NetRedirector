using System.Collections;
using System.Globalization;

namespace NetRedirector.Core;

public enum FirewallStatusKind
{
    NotRequired,
    Clear,
    Review,
    ExplicitBlock,
    Unknown
}

public sealed record FirewallStatus(FirewallStatusKind Kind, string Summary, string Details)
{
    public bool IsWarning => Kind is FirewallStatusKind.Review or FirewallStatusKind.ExplicitBlock or FirewallStatusKind.Unknown;
}

/// <summary>
/// Performs a read-only, conservative assessment of Windows Defender Firewall.
/// It never creates, edits, enables, or disables firewall rules.
/// </summary>
public static class FirewallAssessment
{
    private const int ProfileDomain = 1;
    private const int ProfilePrivate = 2;
    private const int ProfilePublic = 4;
    private const int ProfileAll = int.MaxValue;
    private const int DirectionInbound = 1;
    private const int DirectionOutbound = 2;
    private const int ActionBlock = 0;
    private const int ActionAllow = 1;
    private const int ProtocolAny = 0;
    private const int ProtocolTcp = 6;
    private const int ProtocolUdp = 17;
    private const int ProtocolAnyExplicit = 256;

    public static FirewallStatus Check(RedirectConfig config, string? applicationPath = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var requirements = BuildRequirements(config);
        if (requirements.Count == 0)
        {
            return new FirewallStatus(
                FirewallStatusKind.NotRequired,
                "Firewall: n/a",
                "This redirect uses serial endpoints only; Windows Firewall does not filter the serial link.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return Unknown("Windows Firewall inspection is only available on Windows.");
        }

        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType is null)
            {
                return Unknown("Windows Firewall policy API is not registered.");
            }

            dynamic policy = Activator.CreateInstance(policyType)
                ?? throw new InvalidOperationException("Could not open the Windows Firewall policy API.");

            var currentProfiles = Convert.ToInt32(policy.CurrentProfileTypes, CultureInfo.InvariantCulture);
            var profiles = (List<FirewallProfile>)ReadProfiles(policy, currentProfiles);
            if (profiles.Count == 0)
            {
                return Unknown("Windows did not report an active firewall profile.");
            }

            var rules = (List<FirewallRuleSnapshot>)ReadRules(policy.Rules);
            var executable = NormalizePath(applicationPath ?? Environment.ProcessPath);
            var enabledProfiles = profiles.Where(profile => profile.Enabled).ToArray();

            if (enabledProfiles.Length == 0)
            {
                return new FirewallStatus(
                    FirewallStatusKind.Clear,
                    "Firewall: off",
                    "The active Windows Firewall profiles are disabled. No firewall rule is blocking this redirect, but the machine is not firewall-protected.");
            }

            var assessments = requirements
                .Select(requirement => AssessRequirement(requirement, enabledProfiles, rules, executable))
                .ToArray();

            if (assessments.Any(item => item.ExplicitBlock))
            {
                return new FirewallStatus(
                    FirewallStatusKind.ExplicitBlock,
                    "Firewall: blocked",
                    "An enabled Windows Firewall block rule matches this app, protocol, direction, and endpoint. No rule was changed.");
            }

            if (assessments.Any(item => !item.Covered))
            {
                var missing = string.Join(", ", assessments
                    .Where(item => !item.Covered)
                    .Select(item => item.Requirement.DisplayName)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
                return new FirewallStatus(
                    FirewallStatusKind.Review,
                    "Firewall: review",
                    $"Firewall is enabled, but no matching allow/default rule was found for: {missing}. This is a warning, not proof that traffic is blocked; a network test is the final check.");
            }

            var coveredBy = string.Join("; ", assessments.Select(item => item.CoverageDescription));
            return new FirewallStatus(
                FirewallStatusKind.Clear,
                "Firewall: clear",
                $"The active firewall profiles allow the redirect requirements. {coveredBy} No firewall rules were changed.");
        }
        catch (Exception exception)
        {
            return Unknown($"Could not read Windows Firewall policy: {exception.Message}");
        }
    }

    private static List<FirewallRequirement> BuildRequirements(RedirectConfig config)
    {
        var requirements = new List<FirewallRequirement>();
        AddRequirement(requirements, config.Source, isSource: true);
        AddRequirement(requirements, config.Target, isSource: false);
        return requirements
            .Distinct()
            .ToList();
    }

    private static void AddRequirement(List<FirewallRequirement> requirements, EndpointConfig endpoint, bool isSource)
    {
        switch (endpoint.Protocol)
        {
            case EndpointProtocol.Udp:
                requirements.Add(isSource
                    ? new FirewallRequirement(DirectionInbound, ProtocolUdp, endpoint.Port, $"inbound UDP/{endpoint.Port}")
                    : new FirewallRequirement(DirectionOutbound, ProtocolUdp, endpoint.Port, $"outbound UDP/{endpoint.Port}"));
                break;
            case EndpointProtocol.TcpClient:
                requirements.Add(new FirewallRequirement(DirectionOutbound, ProtocolTcp, endpoint.Port, $"outbound TCP/{endpoint.Port}"));
                break;
            case EndpointProtocol.TcpServer:
                requirements.Add(new FirewallRequirement(DirectionInbound, ProtocolTcp, endpoint.Port, $"inbound TCP/{endpoint.Port}"));
                break;
        }
    }

    private static List<FirewallProfile> ReadProfiles(dynamic policy, int currentProfiles)
    {
        var profiles = new List<FirewallProfile>();
        foreach (var profile in new[] { ProfileDomain, ProfilePrivate, ProfilePublic })
        {
            if ((currentProfiles & profile) == 0)
            {
                continue;
            }

            profiles.Add(new FirewallProfile(
                profile,
                Convert.ToBoolean(policy.FirewallEnabled(profile), CultureInfo.InvariantCulture),
                Convert.ToInt32(policy.DefaultInboundAction(profile), CultureInfo.InvariantCulture),
                Convert.ToInt32(policy.DefaultOutboundAction(profile), CultureInfo.InvariantCulture)));
        }

        return profiles;
    }

    private static List<FirewallRuleSnapshot> ReadRules(dynamic rawRules)
    {
        var rules = new List<FirewallRuleSnapshot>();
        foreach (var rawRule in (IEnumerable)rawRules)
        {
            try
            {
                dynamic rule = rawRule;
                if (!Convert.ToBoolean(rule.Enabled, CultureInfo.InvariantCulture))
                {
                    continue;
                }

                rules.Add(new FirewallRuleSnapshot(
                    Convert.ToInt32(rule.Profiles, CultureInfo.InvariantCulture),
                    Convert.ToInt32(rule.Direction, CultureInfo.InvariantCulture),
                    Convert.ToInt32(rule.Action, CultureInfo.InvariantCulture),
                    Convert.ToInt32(rule.Protocol, CultureInfo.InvariantCulture),
                    Convert.ToString(rule.ApplicationName, CultureInfo.InvariantCulture) ?? "",
                    Convert.ToString(rule.LocalPorts, CultureInfo.InvariantCulture) ?? "",
                    Convert.ToString(rule.RemotePorts, CultureInfo.InvariantCulture) ?? ""));
            }
            catch
            {
                // A malformed or policy-provider-specific rule should not make
                // the indicator claim that the firewall is safe.
            }
        }

        return rules;
    }

    private static RequirementAssessment AssessRequirement(
        FirewallRequirement requirement,
        IReadOnlyList<FirewallProfile> enabledProfiles,
        IReadOnlyList<FirewallRuleSnapshot> rules,
        string? executable)
    {
        var explicitBlock = false;
        var uncovered = false;
        var descriptions = new List<string>();

        foreach (var profile in enabledProfiles)
        {
            var matchingRules = rules.Where(rule =>
                rule.MatchesProfile(profile.Mask) &&
                rule.MatchesApplication(executable) &&
                rule.Matches(requirement)).ToArray();

            if (matchingRules.Any(rule => rule.Action == ActionBlock))
            {
                explicitBlock = true;
                continue;
            }

            if (matchingRules.Any(rule => rule.Action == ActionAllow))
            {
                descriptions.Add($"{requirement.DisplayName} has an app/global allow rule on profile {profile.Name}");
                continue;
            }

            var defaultAction = requirement.Direction == DirectionInbound
                ? profile.DefaultInboundAction
                : profile.DefaultOutboundAction;
            if (defaultAction == ActionAllow)
            {
                descriptions.Add($"{requirement.DisplayName} uses the profile's default allow");
            }
            else
            {
                uncovered = true;
            }
        }

        return new RequirementAssessment(
            requirement,
            explicitBlock,
            !uncovered && !explicitBlock,
            descriptions.Count == 0 ? requirement.DisplayName : string.Join(", ", descriptions.Distinct()));
    }

    private static FirewallStatus Unknown(string details) => new(
        FirewallStatusKind.Unknown,
        "Firewall: unknown",
        $"{details} No firewall rules were changed; test the redirect and check Windows Firewall manually if traffic does not arrive.");

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }

    private readonly record struct FirewallRequirement(int Direction, int Protocol, int Port, string DisplayName);

    private readonly record struct FirewallProfile(int Mask, bool Enabled, int DefaultInboundAction, int DefaultOutboundAction)
    {
        public string Name => Mask switch
        {
            ProfileDomain => "domain",
            ProfilePrivate => "private",
            ProfilePublic => "public",
            _ => "active"
        };
    }

    private readonly record struct RequirementAssessment(
        FirewallRequirement Requirement,
        bool ExplicitBlock,
        bool Covered,
        string CoverageDescription);

    private readonly record struct FirewallRuleSnapshot(
        int Profiles,
        int Direction,
        int Action,
        int Protocol,
        string ApplicationName,
        string LocalPorts,
        string RemotePorts)
    {
        public bool MatchesProfile(int profile) =>
            Profiles == 0 || Profiles == ProfileAll || (Profiles & profile) != 0;

        public bool MatchesApplication(string? executable)
        {
            if (string.IsNullOrWhiteSpace(ApplicationName) || ApplicationName == "*")
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(executable))
            {
                return false;
            }

            return string.Equals(
                NormalizePath(ApplicationName),
                executable,
                StringComparison.OrdinalIgnoreCase);
        }

        public bool Matches(FirewallRequirement requirement)
        {
            if (Direction != requirement.Direction ||
                (Protocol != ProtocolAny && Protocol != ProtocolAnyExplicit && Protocol != requirement.Protocol))
            {
                return false;
            }

            var portSpec = requirement.Direction == DirectionInbound ? LocalPorts : RemotePorts;
            return PortSpecMatches(portSpec, requirement.Port);
        }
    }

    private static bool PortSpecMatches(string? portSpec, int port)
    {
        if (string.IsNullOrWhiteSpace(portSpec) || portSpec is "*" or "Any")
        {
            return true;
        }

        foreach (var part in portSpec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exact) && exact == port)
            {
                return true;
            }

            var range = part.Split('-', 2, StringSplitOptions.TrimEntries);
            if (range.Length == 2 &&
                int.TryParse(range[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) &&
                int.TryParse(range[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var end) &&
                port >= Math.Min(start, end) && port <= Math.Max(start, end))
            {
                return true;
            }
        }

        return false;
    }
}
