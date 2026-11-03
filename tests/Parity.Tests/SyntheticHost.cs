using System.Text.Json;
using System.Text.Json.Nodes;

namespace Parity.Tests;

/// <summary>
/// A synthetic backend host with a per-call log (#36): every call is logged with its request, reply, serialized dataset, session,
/// user, category, type, time and duration, the way the real host's log table holds them. Its state is one policy, its products'
/// riders and a key sequence, so a fresh host answers a scenario the same way every time. The table filter a client sends rides
/// outside the request (a header), as on the real host, so the log never shows it.
/// </summary>
internal sealed class SyntheticHost
{
    private readonly List<JsonObject> _log = new();
    private DateTimeOffset _clock = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
    private int _sessions, _keys = 100;
    private readonly List<(string Code, int Key)> _policyRiders = new();

    public const string PolicyStart = "2024-01-01";

    /// <summary>One call. <paramref name="tables"/> is the header that filters the reply's dataset; null means every table.</summary>
    public string Call(string? session, string user, string category, string type, string request, string[]? tables = null)
    {
        _clock = _clock.AddSeconds(1);
        string? reply = null, dataset = null, error = null;
        switch ($"{category}/{type}")
        {
            case "Auth/Login":
                session = $"S-{++_sessions:000}-{Guid.NewGuid():N}"[..14];
                reply = $"<LoginReply><SessionId>{session}</SessionId><Expires>3600</Expires></LoginReply>";
                break;
            case "Policy/GetPolicy":
                dataset = Filter(tables,
                    ("Policy", $"<Policy><Id>7</Id><Product>3</Product><StartDate>{PolicyStart}</StartDate><Holder>Ada Lovelace</Holder></Policy>"),
                    ("Audit", "<Audit><At>2026-10-01</At><By>clerk</By></Audit><Audit><At>2026-10-02</At><By>clerk</By></Audit>"));
                dataset = "<NewDataSet><xs:schema id=\"NewDataSet\" xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"NewDataSet\" /></xs:schema>" + dataset + "</NewDataSet>";
                break;
            case "Product/GetRiders":
                dataset = JsonSerializer.Serialize(new Dictionary<string, object> { ["Rider"] = new[] { new { Code = "ADB", Max = 50000 }, new { Code = "WOP", Max = 0 } } });
                break;
            case "Product/GetLimits":
                dataset = JsonSerializer.Serialize(new Dictionary<string, object> { ["Limit"] = new[] { new { Min = 1000, Max = 250000 } } });
                break;
            case "Policy/SaveRider":
                var code = request.Split("<Code>")[1].Split("</Code>")[0];
                _policyRiders.Add((code, ++_keys));
                reply = $"<SaveRiderReply><RiderKey>{_keys}</RiderKey></SaveRiderReply>";
                break;
            case "Policy/GetPolicyRiders":
                dataset = "<NewDataSet>" + string.Concat(_policyRiders.Select(r => $"<PolicyRider><Code>{r.Code}</Code><Key>{r.Key}</Key></PolicyRider>")) + "</NewDataSet>";
                break;
            default:
                error = $"unknown call {category}/{type}";
                break;
        }
        _log.Add(new JsonObject
        {
            ["time"] = _clock.ToString("O"), ["session"] = session, ["user"] = user, ["category"] = category, ["type"] = type,
            ["request"] = request, ["reply"] = reply, ["dataset"] = dataset, ["error"] = error, ["durationMs"] = 12.5,
        });
        return reply ?? dataset ?? "";
    }

    private static string Filter(string[]? tables, params (string Name, string Rows)[] all) =>
        string.Concat(all.Where(t => tables == null || tables.Contains(t.Name)).Select(t => t.Rows));

    /// <summary>The log, dumped as JSON lines, the shape parity oracle export reads.</summary>
    public void Dump(string path) => File.WriteAllText(path, string.Join("\n", _log.Select(r => r.ToJsonString())) + "\n");

    public static string SessionOf(string loginReply) => loginReply.Split("<SessionId>")[1].Split("</SessionId>")[0];
}

/// <summary>The legacy client's "add a rider" scenario: six calls, one of them a write. What the recording is made from.</summary>
internal static class LegacyClient
{
    public static string AddRider(SyntheticHost host)
    {
        var s = SyntheticHost.SessionOf(host.Call(null, "agent1", "Auth", "Login", "<Login><User>agent1</User><Workstation>WS-01</Workstation></Login>"));
        var policy = host.Call(s, "agent1", "Policy", "GetPolicy", $"<GetPolicy><SessionId>{s}</SessionId><PolicyId>7</PolicyId></GetPolicy>", new[] { "Policy", "Audit" });
        var start = policy.Split("<StartDate>")[1].Split("</StartDate>")[0];
        host.Call(s, "agent1", "Product", "GetRiders", $"<GetRiders><SessionId>{s}</SessionId><ProductId>3</ProductId></GetRiders>");
        host.Call(s, "agent1", "Product", "GetLimits", $"<GetLimits><SessionId>{s}</SessionId><ProductId>3</ProductId><Rider>ADB</Rider></GetLimits>");
        host.Call(s, "agent1", "Policy", "SaveRider",
            $"<SaveRider><SessionId>{s}</SessionId><PolicyId>7</PolicyId><Rider><Code>ADB</Code><EffectiveDate>{start}</EffectiveDate><Amount>25000</Amount></Rider>"
            + "<Audit><Reason>client request</Reason><Channel>desk</Channel></Audit></SaveRider>");
        host.Call(s, "agent1", "Policy", "GetPolicyRiders", $"<GetPolicyRiders><SessionId>{s}</SessionId><PolicyId>7</PolicyId></GetPolicyRiders>");
        return s;
    }
}

/// <summary>
/// The new client's port of the same scenario, with three seeded differences, each switched off by its fix: it never asks for the
/// limits, it leaves the audit block out of the save, and it takes the rider's effective date from its own clock instead of the
/// policy's start date. It also reads the riders before the policy (reads may reorder) and asks only for the Policy table.
/// </summary>
internal sealed class NewClient(SyntheticHost host, DateOnly today)
{
    public bool SkipsLimits = true, OmitsAudit = true, DatesFromClock = true;

    public void AddRider()
    {
        var s = SyntheticHost.SessionOf(host.Call(null, "agent1", "Auth", "Login", "<Login><User>agent1</User><Workstation>WS-01</Workstation></Login>"));
        host.Call(s, "agent1", "Product", "GetRiders", $"<GetRiders><SessionId>{s}</SessionId><ProductId>3</ProductId></GetRiders>");
        var policy = host.Call(s, "agent1", "Policy", "GetPolicy", $"<GetPolicy><SessionId>{s}</SessionId><PolicyId>7</PolicyId></GetPolicy>", new[] { "Policy" });
        var start = policy.Split("<StartDate>")[1].Split("</StartDate>")[0];
        if (!SkipsLimits) host.Call(s, "agent1", "Product", "GetLimits", $"<GetLimits><SessionId>{s}</SessionId><ProductId>3</ProductId><Rider>ADB</Rider></GetLimits>");
        var date = DatesFromClock ? today.ToString("yyyy-MM-dd") : start;
        host.Call(s, "agent1", "Policy", "SaveRider",
            $"<SaveRider><SessionId>{s}</SessionId><PolicyId>7</PolicyId><Rider><Code>ADB</Code><EffectiveDate>{date}</EffectiveDate><Amount>25000</Amount></Rider>"
            + (OmitsAudit ? "" : "<Audit><Reason>client request</Reason><Channel>desk</Channel></Audit>") + "</SaveRider>");
        host.Call(s, "agent1", "Policy", "GetPolicyRiders", $"<GetPolicyRiders><SessionId>{s}</SessionId><PolicyId>7</PolicyId></GetPolicyRiders>");
    }
}
