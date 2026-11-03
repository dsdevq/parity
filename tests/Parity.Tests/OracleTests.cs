using System.Diagnostics;
using System.Text.Json;
using Parity.Lint;
using Parity.Model;
using Parity.Oracle;
using Xunit;

namespace Parity.Tests;

/// <summary>
/// Issue #36, the oracle layer: the legacy client's backend calls recorded once per scenario from the host's per-call log, the new
/// client's calls replayed and diffed against them, and a scenario verified only by a green replay with an empty diff.
/// </summary>
[Collection("extract")]
public sealed class OracleTests
{
    private readonly ExtractFixture _f;
    public OracleTests(ExtractFixture f) => _f = f;

    private const string Scn = "scn:rider-added";
    private const string Key = "test-redaction-key";
    private static readonly DateOnly Today = new(2026, 11, 3);

    private static OracleConfig Config() => new()
    {
        Writes = new() { "Policy/Save*" },
        Redact = new() { new RedactRule { Target = "any", Field = "**/User" }, new RedactRule { Target = "dataset", Field = "Policy/Holder" } },
    };

    /// <summary>A module copy with the scenario in its registry and the limits service mapped to its wire call.</summary>
    private string Module(string name)
    {
        var dir = _f.ModuleCopy(name);
        var scenarios = Path.Combine(dir, "registry", "scenarios.json");
        File.WriteAllText(scenarios, File.ReadAllText(scenarios).Replace("  \"scenarios\": [\n", """
          "scenarios": [
            {
              "id": "scn:rider-added",
              "version": 1,
              "title": "an agent adds a rider to a policy from the riders step",
              "role": "agent",
              "rights": ["policy.edit"],
              "negative": false,
              "exercises": ["svc:IPolicyService.GetAvailableRiders", "svc:IPolicyService.GetPremiumLimits"],
              "cite": ["handler:CoverSummaryForm/btnConfirm_Click"]
            },

        """.Replace("\r\n", "\n")));
        var deps = Path.Combine(dir, "registry", "dependencies.json");
        File.WriteAllText(deps, File.ReadAllText(deps).Replace("{ \"id\": \"svc:IPolicyService.GetPremiumLimits\", \"errors\": [] }",
            "{ \"id\": \"svc:IPolicyService.GetPremiumLimits\", \"errors\": [], \"wire\": [\"Product/GetLimits\"] }"));
        return dir;
    }

    /// <summary>Writes the oracle settings to recordings/oracle.json, and a decision that cites their hash.</summary>
    private static void WriteSettings(string moduleDir, OracleConfig cfg, bool decide = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OracleModule.SettingsPath(moduleDir))!);
        File.WriteAllText(OracleModule.SettingsPath(moduleDir), JsonSerializer.Serialize(cfg, Json.Options));
        Assert.Equal(OracleModule.SettingsHash(cfg), OracleModule.SettingsHash(OracleModule.LoadSettings(moduleDir)));
        if (decide) Decide(moduleDir, cfg);
    }

    private static void Decide(string moduleDir, OracleConfig cfg)
    {
        var hash = OracleModule.SettingsHash(cfg);
        var decisions = Path.Combine(moduleDir, "registry", "decisions.json");
        File.WriteAllText(decisions, File.ReadAllText(decisions).Replace("  \"decisions\": [\n", $$"""
          "decisions": [
            {
              "id": "dec:oracle-settings-{{hash["sha256:".Length..]}}",
              "question": "May the verification trust these oracle settings (replay command, writes, redaction)?",
              "answer": "Yes. A person reviewed them.",
              "owner": "alex",
              "date": "2026-11-03",
              "cite": ["oracle:{{hash}}"]
            },

        """.Replace("\r\n", "\n")));
    }

    private static void Phase(string moduleDir, string phase) => File.WriteAllText(Path.Combine(Path.GetDirectoryName(moduleDir)!, ".phase"), phase + "\n");

    /// <summary>The legacy client's run, between other sessions' calls, dumped as the host log.</summary>
    private static (string Log, string Session) RecordLegacy(string dir)
    {
        var host = new SyntheticHost();
        host.Call(null, "night-shift", "Auth", "Login", "<Login><User>night-shift</User><Workstation>WS-09</Workstation></Login>");
        var session = LegacyClient.AddRider(host);
        host.Call(null, "night-shift", "Auth", "Login", "<Login><User>night-shift</User><Workstation>WS-09</Workstation></Login>");
        var log = Path.Combine(dir, "legacy-run.jsonl");
        host.Dump(log);
        return (log, session);
    }

    private static string Capture(string path, Action<NewClient> setup)
    {
        var host = new SyntheticHost();
        var client = new NewClient(host, Today);
        setup(client);
        client.AddRider();
        host.Dump(path);
        return path;
    }

    private sealed class DelegateRunner(Func<ReplayRun, int> run) : IReplayRunner
    {
        public int Run(ReplayRun r) => run(r);
    }

    [Fact]
    public void issue_36_a_recorded_scenario_diffs_the_new_client_to_its_seeded_differences_and_verifies_after_the_fixes()
    {
        var dir = Module("36");
        var scratch = Path.Combine(_f.OutRoot, "copies", "36", "scratch");
        Directory.CreateDirectory(scratch);
        var (log, session) = RecordLegacy(scratch);
        var cfg = Config();
        ExportRequest Export(List<string>? sessions = null) => new(dir, Scn, "recording", new() { log }, sessions ?? new() { session }, null, null, "alex", "local", "4.2.1", false, cfg, Key, Today);

        // record: only in the record phase, only selectively, only with a redaction key when rules exist
        Phase(dir, "implement");
        Assert.Contains("record phase", Assert.Throws<InvalidOperationException>(() => OracleModule.Export(Export())).Message);
        Phase(dir, "record");
        Assert.Contains("selective", Assert.Throws<InvalidOperationException>(() => OracleModule.Export(Export(new()))).Message);
        Assert.Contains("PARITY_REDACT_KEY", Assert.Throws<InvalidOperationException>(() => OracleModule.Export(Export() with { RedactKey = null })).Message);
        var (recDir, rec) = OracleModule.Export(Export());
        Assert.Equal(new[] { "Auth/Login", "Policy/GetPolicy", "Product/GetRiders", "Product/GetLimits", "Policy/SaveRider", "Policy/GetPolicyRiders" }, rec.Calls.Select(c => c.Key));
        Assert.Equal(new[] { 5 }, rec.Calls.Where(c => c.Write).Select(c => c.Seq));
        Assert.Equal(new[] { "alex", "local", "4.2.1", "2026-11-03" }, new[] { rec.Provenance.RecordedBy, rec.Provenance.Environment, rec.Provenance.LegacyBuild, rec.Provenance.Recorded });
        Assert.True(rec.Redacted >= 2);
        var written = Directory.EnumerateFiles(recDir, "*", SearchOption.AllDirectories).Select(File.ReadAllText).ToList();
        Assert.DoesNotContain(written, t => t.Contains("agent1") || t.Contains("Ada Lovelace") || t.Contains("night-shift") || t.Contains("WS-09"));
        Assert.Contains(written, t => t.Contains("<Workstation>WS-01</Workstation>"));                       // what no rule names stays as logged
        Assert.Contains("exists", Assert.Throws<InvalidOperationException>(() => OracleModule.Export(Export())).Message);

        // the oracle settings decide what a verification trusts: undecided, the diff refuses; with a decision citing their hash, it runs
        Phase(dir, "implement");
        WriteSettings(dir, cfg, decide: false);
        Assert.Contains("ORACLE-UNDECIDED", Assert.Throws<InvalidOperationException>(() => OracleModule.Diff(dir, Scn, Capture(Path.Combine(scratch, "new-0.jsonl"), _ => { }), cfg, Key, Today)).Message);
        Assert.Contains(Registry.Check(dir, gate: false), x => x.Code == "ORACLE-UNDECIDED" && x.Message.Contains(OracleModule.SettingsHash(cfg)));
        Decide(dir, cfg);
        Assert.DoesNotContain(Registry.Check(dir, gate: false), x => x.Code is "ORACLE-UNDECIDED" or "CITE-UNKNOWN");

        // replay the seeded new client: without a rule, the session id differs in every call
        var raw = OracleModule.Diff(dir, Scn, Capture(Path.Combine(scratch, "new-1.jsonl"), _ => { }), cfg, Key, Today);
        Assert.Contains(raw.Differences, d => d.Path != null && d.Path.EndsWith("SessionId", StringComparison.Ordinal));

        // a normalization rule without a decision is refused; with one, the diff reports exactly the three seeded differences
        var rules = Normalization.PathOf(dir);
        File.WriteAllText(rules, """
        {
          "rules": [
            { "id": "norm:session-id", "decision": "dec:session-id-differs-every-login", "scope": "global", "target": "any", "field": "**/SessionId", "match": null }
          ]
        }
        """);
        Assert.Contains("NORM-UNDECIDED", Assert.Throws<InvalidOperationException>(() => OracleModule.Diff(dir, Scn, null, cfg, Key, Today)).Message);
        Assert.Contains(Registry.Check(dir, gate: false), x => x.Code == "NORM-UNDECIDED");
        var decisions = Path.Combine(dir, "registry", "decisions.json");
        File.WriteAllText(decisions, File.ReadAllText(decisions).Replace("  \"decisions\": [\n", """
          "decisions": [
            {
              "id": "dec:session-id-differs-every-login",
              "question": "The session id is different on every login, in both clients. May the diff ignore its value?",
              "answer": "Yes. It is issued by the host per login.",
              "owner": "alex",
              "date": "2026-11-03",
              "cite": ["norm:session-id"]
            },

        """.Replace("\r\n", "\n")));
        var diff = OracleModule.Diff(dir, Scn, null, cfg, Key, Today);
        Assert.Equal(3, diff.Differences.Count);
        Assert.Contains(diff.Differences, d => d is { Part: "call", Kind: "missing", LegacySeq: 4, Call: "Product/GetLimits" });
        Assert.Contains(diff.Differences, d => d is { Part: "request", Kind: "missing", LegacySeq: 5, Path: "SaveRider/Audit", Fields: 2 });
        Assert.Contains(diff.Differences, d => d is { Part: "request", Kind: "changed", LegacySeq: 5, Path: "SaveRider/Rider/EffectiveDate", Legacy: SyntheticHost.PolicyStart, New: "2026-11-03" });
        Assert.Equal(10, diff.Rules.Masked["norm:session-id"]);                                               // 4 paired requests and the login reply, both sides
        var report = OracleDiff.Render(diff, OracleModule.Where(dir));
        Assert.StartsWith("scn:rider-added: 6 recorded call(s), 5 replayed, 3 difference(s)\n", report);
        Assert.Contains("1. missing call: #4 Product/GetLimits", report);
        Assert.Contains("legacy client: svc:IPolicyService.GetPremiumLimits, called from", report);            // where the legacy client makes the call
        Assert.Contains("2. request block missing: #5 Policy/SaveRider SaveRider/Audit (2 fields)", report);
        Assert.Contains("legacy: \"2024-01-01\"\n   new: \"2026-11-03\"", report);
        Assert.Contains("not compared (legacy reply tables the new client does not ask for): #2 Policy/GetPolicy: Audit", report);

        // a recording edited after export is refused by the diff and by the registry check
        var request = Path.Combine(recDir, "calls", "005.request.xml");
        var original = File.ReadAllText(request);
        File.WriteAllText(request, original.Replace("<Audit><Reason>client request</Reason><Channel>desk</Channel></Audit>", ""));
        Assert.Contains("no longer matches its seal", Assert.Throws<InvalidOperationException>(() => OracleModule.Diff(dir, Scn, null, cfg, Key, Today)).Message);
        Assert.Contains(Registry.Check(dir, gate: false), x => x.Code == "RECORDING-TAMPERED" && x.Message.Contains("calls/005.request.xml"));
        File.WriteAllText(request, original);

        // a checkout that converts line endings (core.autocrlf) is not an edit
        foreach (var f in Directory.EnumerateFiles(recDir, "*", SearchOption.AllDirectories)) File.WriteAllText(f, File.ReadAllText(f).Replace("\r\n", "\n").Replace("\n", "\r\n"));
        Assert.DoesNotContain(Registry.Check(dir, gate: false), x => x.Code is "RECORDING-TAMPERED" or "RECORDING-INVALID");
        Assert.Equal(3, OracleModule.Diff(dir, Scn, null, cfg, Key, Today).Differences.Count);

        // verify: a red replay or a replay with differences leaves the scenario unverified; after the fixes it is verified, no human step
        var fixes = new NewClient(new SyntheticHost(), Today);
        IReplayRunner Runner(int exit) => new DelegateRunner(r => { Capture(r.Capture, c => { c.SkipsLimits = fixes.SkipsLimits; c.OmitsAudit = fixes.OmitsAudit; c.DatesFromClock = fixes.DatesFromClock; }); return exit; });
        var red = OracleModule.Verify(dir, Scn, cfg, Runner(1), Key, Today);
        Assert.False(red.Verified);
        Assert.Contains("tests are not green", red.Text);
        var seeded = OracleModule.Verify(dir, Scn, cfg, Runner(0), Key, Today);
        Assert.False(seeded.Verified);
        Assert.Equal(1, seeded.ExitCode);
        Assert.Contains("not verified: 3 difference(s)", seeded.Text);
        Assert.False(File.Exists(Recording.VerifiedFile(dir, Scn)));
        Assert.DoesNotContain(Registry.Scenarios(dir), s => s.Id == Scn && s.LastVerified != null);

        fixes.SkipsLimits = false; fixes.OmitsAudit = false; fixes.DatesFromClock = false;
        var green = OracleModule.Verify(dir, Scn, cfg, Runner(0), Key, Today);
        Assert.True(green.Verified, green.Text);
        Assert.Contains("scn:rider-added: verified 2026-11-03: 6 call(s), 0 differences, tests green", green.Text);
        Assert.Equal("2026-11-03", Registry.Scenarios(dir).Single(s => s.Id == Scn).LastVerified);
        Assert.True(File.Exists(Recording.VerifiedFile(dir, Scn)));
        var scenariosText = File.ReadAllText(Path.Combine(dir, "registry", "scenarios.json"));
        Assert.Contains("      \"cite\": [\"handler:CoverSummaryForm/btnConfirm_Click\"],\n      \"lastVerified\": \"2026-11-03\"\n    },", scenariosText);  // the file keeps its layout
        Assert.DoesNotContain(Registry.Check(dir, gate: false), x => x.Severity == "error");
        var verification = JsonSerializer.Deserialize<Verification>(File.ReadAllText(Recording.VerifiedFile(dir, Scn)), Json.Options)!;
        Assert.Equal(new[] { "#2 Policy/GetPolicy: Audit" }, verification.NotCompared);                          // the skipped tables are on the record

        // only verify sets lastVerified on a recorded scenario: a hand-set date, or a rule change after verifying, is not backed
        File.WriteAllText(Path.Combine(dir, "registry", "scenarios.json"), scenariosText.Replace("\"lastVerified\": \"2026-11-03\"", "\"lastVerified\": \"2026-11-04\""));
        Assert.Contains(Registry.Check(dir, gate: false), x => x.Code == "SCENARIO-VERIFY-UNBACKED" && x.Message.Contains("dated 2026-11-03"));
        File.WriteAllText(Path.Combine(dir, "registry", "scenarios.json"), scenariosText);
        var rulesText = File.ReadAllText(rules);
        File.WriteAllText(rules, rulesText.Replace("\"match\": null", "\"match\": \"S-.*\""));
        Assert.Contains(Registry.Check(dir, gate: false), x => x.Code == "SCENARIO-VERIFY-UNBACKED" && x.Message.Contains("normalization rules changed"));
        File.WriteAllText(rules, rulesText);
        Assert.DoesNotContain(Registry.Check(dir, gate: false), x => x.Severity == "error");

        // changing the oracle settings (here: no write is a write any more) needs a decision, and unbacks the verification either way
        var loosened = Config(); loosened.Writes = new();
        WriteSettings(dir, loosened, decide: false);
        var loose = Registry.Check(dir, gate: false);
        Assert.Contains(loose, x => x.Code == "ORACLE-UNDECIDED");
        Assert.Contains(loose, x => x.Code == "SCENARIO-VERIFY-UNBACKED" && x.Message.Contains("oracle settings"));
        Assert.Contains("ORACLE-UNDECIDED", Assert.Throws<InvalidOperationException>(() => OracleModule.Verify(dir, Scn, loosened, Runner(0), Key, Today)).Message);
        Decide(dir, loosened);
        Assert.DoesNotContain(Registry.Check(dir, gate: false), x => x.Code == "ORACLE-UNDECIDED");
        Assert.Contains(Registry.Check(dir, gate: false), x => x.Code == "SCENARIO-VERIFY-UNBACKED" && x.Message.Contains("oracle settings"));
        WriteSettings(dir, cfg, decide: false);
        Assert.DoesNotContain(Registry.Check(dir, gate: false), x => x.Severity == "error");

        // a scenario whose version moved on since it was recorded is neither diffed nor verified: it is recorded again first
        var scenariosPath = Path.Combine(dir, "registry", "scenarios.json");
        var bumped = System.Text.RegularExpressions.Regex.Replace(scenariosText, "(\"id\": \"scn:rider-added\",\\s*\"version\": )1", "${1}2");
        Assert.NotEqual(scenariosText, bumped);
        File.WriteAllText(scenariosPath, bumped);
        var stale = Assert.Throws<InvalidOperationException>(() => OracleModule.Verify(dir, Scn, cfg, Runner(0), Key, Today));
        Assert.Contains("made at version 1; the scenario is at version 2", stale.Message);
        Assert.Contains("record it again", stale.Message);
        Assert.Contains("made at version 1", Assert.Throws<InvalidOperationException>(() => OracleModule.Diff(dir, Scn, null, cfg, Key, Today)).Message);
        File.WriteAllText(scenariosPath, scenariosText);
    }

    [Fact]
    public void issue_36_oracle_settings_with_an_unknown_key_or_a_wrong_type_are_refused_not_ignored()
    {
        var dir = Module("36oraclebad");
        Assert.Equal("none", OracleModule.SettingsHash(OracleModule.LoadSettings(dir)));                              // absent: sets nothing
        var path = OracleModule.SettingsPath(dir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        foreach (var bad in new[] { "{ \"wirtes\": [] }", "{ \"writes\": \"Policy/Save*\" }", "[]", "{ \"writes\": [" })
        {
            File.WriteAllText(path, bad);
            Assert.Contains("recordings/oracle.json", Assert.Throws<InvalidOperationException>(() => OracleModule.LoadSettings(dir)).Message);
            Assert.Contains(Registry.Check(dir, gate: false), x => x.Code == "ORACLE-INVALID");
        }
        File.WriteAllText(path, "{ // comments and formatting do not change the hash\n  \"writes\": [\"A/B\"] }");
        Assert.Equal(OracleModule.SettingsHash(new OracleConfig { Writes = new() { "A/B" } }), OracleModule.SettingsHash(OracleModule.LoadSettings(dir)));
        Assert.DoesNotContain(Registry.Check(dir, gate: false), x => x.Code == "ORACLE-INVALID");
    }

    [Fact]
    public void issue_36_guard_keeps_recordings_and_verifications_out_of_reach_of_agent_writes()
    {
        if (GuardHookTests.Sh() == null) return; // no POSIX shell: the hook cannot run here at all
        var baseDir = Path.Combine(Path.GetTempPath(), "parity-oracle-guard-" + Guid.NewGuid().ToString("N")[..8]);
        var work = Path.Combine(baseDir, "repo");
        Directory.CreateDirectory(Path.Combine(work, "legacy", "hooks"));
        Directory.CreateDirectory(Path.Combine(work, "legacy", "M", "recordings", "rider-added"));
        File.Copy(Path.Combine(_f.RepoRoot, "adopt", "hooks", "guard-legacy.sh"), Path.Combine(work, "legacy", "hooks", "guard-legacy.sh"));
        File.WriteAllText(Path.Combine(work, "legacy", "extract.config.json"), "{\n  \"project\": \"" + Path.Combine(baseDir, "Legacy", "App").Replace('\\', '/') + "/App.csproj\",\n  \"output\": \".\"\n}\n");
        string Call(string tool, object input, string? cwd = null) =>
            JsonSerializer.Serialize(new Dictionary<string, object> { ["cwd"] = cwd ?? work, ["hook_event_name"] = "PreToolUse", ["tool_name"] = tool, ["tool_input"] = input });
        var rec = "legacy/M/recordings/rider-added/calls/005.request.xml";
        try
        {
            foreach (var phase in new[] { "implement", "specify", "map", "extract", "bug", "" })
            {
                File.WriteAllText(Path.Combine(work, "legacy", ".phase"), phase + "\n");
                foreach (var (tool, input, cwd) in new (string, object, string?)[]
                {
                    ("Write", new { file_path = rec, content = "<SaveRider/>" }, null),
                    ("Edit", new { file_path = Path.Combine(work, "legacy", "M", "recordings", "normalization.json"), old_string = "1", new_string = "2" }, null),
                    ("Edit", new { file_path = Path.Combine(work, "legacy", "M", "recordings", "oracle.json"), old_string = "\"writes\": [\"Policy/Save*\"]", new_string = "\"writes\": []" }, null),
                    ("Bash", new { command = "echo '{\"writes\":[]}' > recordings/oracle.json" }, Path.Combine(work, "legacy", "M")),
                    ("Bash", new { command = "sed -i 's/2024/2026/' " + rec }, null),
                    ("Bash", new { command = "cd legacy/M && rm -r recordings" }, null),
                    ("Bash", new { command = "echo '{}' > recordings/normalization.json" }, Path.Combine(work, "legacy", "M")),
                    ("Bash", new { command = "cp x.json $M/recordings/a.json" }, null),
                    ("Bash", new { command = "cd legacy/M && echo x > verified/a.json" }, null),
                    ("Bash", new { command = "echo x>verified/a.json" }, Path.Combine(work, "legacy", "M")),
                    ("Bash", new { command = "cd legacy/M && echo x>verified/a.json" }, null),
                    ("Bash", new { command = "cd legacy/M;echo x >verified/a.json" }, null),
                    ("Bash", new { command = "echo x > verif\"\"ied/a.json" }, Path.Combine(work, "legacy", "M")),
                    ("Bash", new { command = "cd legacy/M && echo x > verif''ied/a.json" }, null),
                    ("Bash", new { command = "echo x > veri\\fied/a.json" }, Path.Combine(work, "legacy", "M")),
                    ("Bash", new { command = "echo x > v*/a.json" }, Path.Combine(work, "legacy", "M")),
                    ("Bash", new { command = "rm -rf ve*" }, Path.Combine(work, "legacy", "M")),
                    ("Bash", new { command = "echo x > verifie?/a.json" }, Path.Combine(work, "legacy", "M")),
                    ("Bash", new { command = "cd legacy/M && rm -rf ve*" }, null),
                    ("Bash", new { command = "rm -rf legacy/M/v*" }, null),
                    ("Bash", new { command = "cd legacy/M; echo x > recordings/oracle.json" }, null),
                    ("Bash", new { command = "echo x > legacy/M/{verified,x}/a.json" }, null),
                    ("Bash", new { command = "echo x > legacy/M/v{er,}ified/a.json" }, null),
                    ("Bash", new { command = "d=verified; echo x > legacy/M/$d/a.json" }, null),
                    ("Bash", new { command = "echo x > legacy/M/$(echo verified)/a.json" }, null),
                    ("Bash", new { command = "echo x > legacy/M/`echo verified`/a.json" }, null),
                    ("Bash", new { command = "echo x > $d/a.json" }, Path.Combine(work, "legacy", "M")),
                    ("Bash", new { command = "cd legacy/M && echo x > {verified,x}/a.json" }, null),
                    ("Bash", new { command = "echo x > verified" }, Path.Combine(work, "legacy", "M")),
                    ("Bash", new { command = "cd legacy/M/registry && echo x > ../verified/a.json" }, null),
                    ("PowerShell", new { command = "Set-Location legacy/M; Set-Content verified/a.json x" }, null),
                    ("Write", new { file_path = "legacy/M/verified/rider-added.json", content = "{}" }, null),
                    ("mcp__filesystem__write_file", new { path = "legacy/M/verified/rider-added.json", content = "{}" }, null),
                })
                {
                    var r = GuardHookTests.Run(work, Call(tool, input, cwd), hideJq: false);
                    Assert.True(r.Code == 2, $"phase '{phase}': {tool} {JsonSerializer.Serialize(input)} should be refused: {r.Err}");
                }
                foreach (var (tool, input) in new (string, object)[]
                {
                    ("Read", new { file_path = rec }),
                    ("Read", new { file_path = "legacy/M/recordings/oracle.json" }),
                    ("Grep", new { pattern = "EffectiveDate", path = "legacy/M/recordings" }),
                    ("Bash", new { command = "parity oracle verify legacy/M scn:rider-added" }),
                    ("Edit", new { file_path = "legacy/M/fixtures/x.json", old_string = "\"shapeVerified\": false", new_string = "\"shapeVerified\": true" }),
                    ("Bash", new { command = "ls legacy/M/registry/*.md" }),
                    ("Bash", new { command = "echo verified results *.json" }),
                    ("Bash", new { command = "echo ${HOME} $(date)" }),
                    ("Bash", new { command = "awk '{print $1}' legacy/M/registry/scenarios.json" }),
                })
                {
                    if (phase == "map" && tool == "Edit") continue;                                                  // map writes only registry/ and evidence/ anyway
                    var r = GuardHookTests.Run(work, Call(tool, input), hideJq: false);
                    Assert.True(r.Code == 0, $"phase '{phase}': {tool} {JsonSerializer.Serialize(input)} should pass: {r.Err}");
                }
            }
            // the record phase: recordings and the rules may change; a verification still may not
            File.WriteAllText(Path.Combine(work, "legacy", ".phase"), "record\n");
            Assert.Equal(0, GuardHookTests.Run(work, Call("Write", new { file_path = rec, content = "<SaveRider/>" }), hideJq: false).Code);
            Assert.Equal(0, GuardHookTests.Run(work, Call("Write", new { file_path = "legacy/M/recordings/oracle.json", content = "{}" }), hideJq: false).Code);
            Assert.Equal(0, GuardHookTests.Run(work, Call("Bash", new { command = "git add legacy/M/recordings/rider-added" }), hideJq: false).Code);
            Assert.Equal(0, GuardHookTests.Run(work, Call("Bash", new { command = "cd legacy/M; echo x > recordings/oracle.json" }), hideJq: false).Code);
            Assert.Equal(0, GuardHookTests.Run(work, Call("Bash", new { command = "rm -rf recordi*" }, Path.Combine(work, "legacy", "M")), hideJq: false).Code);
            foreach (var cmd in new[] { "cd legacy/M && echo x > verified/a.json", "cd legacy/M/registry && echo x > ../verified/a.json", "cd legacy/M;echo x>verified/a.json", "cd legacy/M && echo x > verif\"\"ied/a.json", "cd legacy/M && echo x > v*/a.json", "rm -rf legacy/M/ve*", "echo x > legacy/M/{verified,x}/a.json", "echo x > legacy/M/v{er,}ified/a.json", "d=verified; echo x > legacy/M/$d/a.json", "echo x > legacy/M/$(echo verified)/a.json" })
                Assert.Equal(2, GuardHookTests.Run(work, Call("Bash", new { command = cmd }), hideJq: false).Code);
            var v = GuardHookTests.Run(work, Call("Write", new { file_path = "legacy/M/verified/rider-added.json", content = "{}" }), hideJq: false);
            Assert.Equal(2, v.Code);
            Assert.Contains("written only by parity oracle verify", v.Err);
            Assert.Contains("verified/rider-added.json", File.ReadAllText(Path.Combine(work, "legacy", ".guard-denials.log")));
        }
        finally { try { Directory.Delete(baseDir, true); } catch { } }
    }

    [Fact]
    public void issue_36_the_cli_exports_diffs_and_verifies_through_the_configured_replay_command()
    {
        if (GuardHookTests.Sh() == null) return; // verify runs its command through sh
        var dir = Module("36cli");
        var legacyDir = Path.GetDirectoryName(dir)!;
        var scratch = Path.Combine(_f.OutRoot, "copies", "36cli", "scratch");
        Directory.CreateDirectory(scratch);
        var (log, session) = RecordLegacy(scratch);
        var seeded = Capture(Path.Combine(scratch, "seeded.jsonl"), _ => { });
        var fixedRun = Capture(Path.Combine(scratch, "fixed.jsonl"), c => { c.SkipsLimits = false; c.OmitsAudit = false; c.DatesFromClock = false; });
        void Configure(string replay) => WriteSettings(dir, new OracleConfig { Writes = new() { "Policy/Save*" }, Redact = new() { new RedactRule { Target = "any", Field = "**/User" } }, Replay = replay });
        Configure("cp '" + seeded + "' \"$PARITY_CAPTURE\"");
        var env = new Dictionary<string, string?> { ["PARITY_REDACT_KEY"] = Key };
        Phase(dir, "record");
        var export = Run(legacyDir, env, "oracle", "export", "PolicyWizard", Scn, "--log", log, "--session", session, "--recorded-by", "alex", "--environment", "local", "--legacy-build", "4.2.1");
        Assert.True(export.Code == 0, export.Err);
        Assert.Matches(@"^PolicyWizard/recordings/rider-added\t6 call\(s\)\t1 write\(s\)\t[1-9]\d* value\(s\) redacted\tsha256:[0-9a-f]{16}\n$", export.Out);
        Phase(dir, "implement");
        File.WriteAllText(Normalization.PathOf(dir), "{ \"version\": 1, \"rules\": [ { \"id\": \"norm:session-id\", \"decision\": \"dec:summary-needs-a-beneficiary\", \"scope\": \"global\", \"target\": \"any\", \"field\": \"**/SessionId\" } ] }");
        var undecided = Run(legacyDir, env, "oracle", "diff", "PolicyWizard", Scn, "--capture", seeded);
        Assert.Equal(3, undecided.Code);
        Assert.Contains("does not cite norm:session-id", undecided.Err);
        var decisions = Path.Combine(dir, "registry", "decisions.json");
        File.WriteAllText(decisions, File.ReadAllText(decisions).Replace("\"cite\": [\"handler:CoverSummaryForm/btnConfirm_Click\"]", "\"cite\": [\"handler:CoverSummaryForm/btnConfirm_Click\", \"norm:session-id\"]"));
        var diff = Run(legacyDir, env, "oracle", "diff", "PolicyWizard", Scn, "--capture", seeded);
        Assert.Equal(1, diff.Code);
        Assert.StartsWith("scn:rider-added: 6 recorded call(s), 5 replayed, 3 difference(s)", diff.Out);
        Assert.Equal(1, Run(legacyDir, env, "oracle", "verify", "PolicyWizard", Scn).Code);
        Configure("exit 4");
        var red = Run(legacyDir, env, "oracle", "verify", "PolicyWizard", Scn);
        Assert.Equal(1, red.Code);
        Assert.Contains("failed (exit 4)", red.Out);
        Configure("true");
        var empty = Run(legacyDir, env, "oracle", "verify", "PolicyWizard", Scn);
        Assert.Equal(3, empty.Code);
        Assert.Contains("left no capture", empty.Err);
        Configure("cp '" + fixedRun + "' {capture}");
        var green = Run(legacyDir, env, "oracle", "verify", "PolicyWizard", Scn);
        Assert.True(green.Code == 0, green.Out + green.Err);
        Assert.Contains("verified", green.Out);
        Assert.Equal(0, Run(legacyDir, env, "oracle", "diff", "PolicyWizard", Scn).Code);                           // the last replay, kept redacted
        Assert.DoesNotContain("agent1", string.Concat(Directory.EnumerateFiles(Path.Combine(dir, "replays"), "*", SearchOption.AllDirectories).Select(File.ReadAllText)));
    }

    [Fact]
    public void issue_40_diff_json_prints_every_call_with_its_capture_pairing_and_differences_after_redaction_and_normalization()
    {
        var dir = Module("40");
        var legacyDir = Path.GetDirectoryName(dir)!;
        var scratch = Path.Combine(_f.OutRoot, "copies", "40", "scratch");
        Directory.CreateDirectory(scratch);
        var (log, session) = RecordLegacy(scratch);
        WriteSettings(dir, Config());                                                                          // redacts **/User and Policy/Holder
        var env = new Dictionary<string, string?> { ["PARITY_REDACT_KEY"] = Key };
        Phase(dir, "record");
        var export = Run(legacyDir, env, "oracle", "export", "PolicyWizard", Scn, "--log", log, "--session", session, "--recorded-by", "alex");
        Assert.True(export.Code == 0, export.Err);
        Phase(dir, "implement");

        // the seeded new client, run by another user at another workstation, with one call the legacy client never makes and an
        // error the legacy client never got
        var seeded = Capture(Path.Combine(scratch, "seeded.jsonl"), _ => { });
        var rows = File.ReadAllLines(seeded).Where(l => l.Length > 0).Select(l => System.Text.Json.Nodes.JsonNode.Parse(l)!.AsObject()).ToList();
        var login = rows.Single(r => (string?)r["type"] == "Login");
        login["request"] = ((string)login["request"]!).Replace("agent1", "agent2").Replace("WS-01", "XS-01");
        rows.Single(r => (string?)r["type"] == "GetPolicyRiders")["error"] = "host timeout";
        rows.Add(new System.Text.Json.Nodes.JsonObject { ["time"] = "2026-11-03T10:00:00Z", ["category"] = "Audit", ["type"] = "Ping", ["request"] = "<Ping><Channel>desk</Channel></Ping>" });
        File.WriteAllText(seeded, string.Join("\n", rows.Select(r => r.ToJsonString())) + "\n");

        // a refused diff (a rule nobody decided) is exit 3 with nothing on stdout, so a consumer never reads a partial result
        File.WriteAllText(Normalization.PathOf(dir), """
        { "version": 2, "rules": [
          { "id": "norm:session-id", "decision": "dec:summary-needs-a-beneficiary", "scope": "global", "target": "any", "field": "**/SessionId" },
          { "id": "norm:workstation-number", "decision": "dec:summary-needs-a-beneficiary", "scope": "call:Auth/*", "target": "request", "field": "Login/Workstation", "match": "[0-9]+$" }
        ] }
        """);
        var refused = Run(legacyDir, env, "oracle", "diff", "PolicyWizard", Scn, "--capture", seeded, "--json");
        Assert.Equal(3, refused.Code);
        Assert.Equal("", refused.Out);
        var decisions = Path.Combine(dir, "registry", "decisions.json");
        File.WriteAllText(decisions, File.ReadAllText(decisions).Replace("\"cite\": [\"handler:CoverSummaryForm/btnConfirm_Click\"]",
            "\"cite\": [\"handler:CoverSummaryForm/btnConfirm_Click\", \"norm:session-id\", \"norm:workstation-number\"]"));

        // with differences: exit 1, stdout is the JSON alone (it parses whole), every recorded call in recorded order with the capture
        // call it paired with (or null), then the capture call nothing pairs with
        var diff = Run(legacyDir, env, "oracle", "diff", "PolicyWizard", Scn, "--capture", seeded, "--json");
        Assert.True(diff.Code == 1, diff.Out + diff.Err);
        var root = JsonDocument.Parse(diff.Out).RootElement;
        string S(JsonElement e, string name) => e.GetProperty(name) is var v && v.ValueKind == JsonValueKind.Null ? "null" : v.ValueKind == JsonValueKind.Number ? v.GetInt32().ToString() : v.GetString()!;
        Assert.Equal(OracleDiff.JsonSchema, root.GetProperty("schema").GetInt32());
        Assert.Equal("scn:rider-added 6 6 7", $"{S(root, "scenario")} {S(root, "recordedCalls")} {S(root, "replayedCalls")} {S(root, "differences")}");
        var calls = root.GetProperty("calls").EnumerateArray().Select(c =>
            $"{S(c, "recordedSeq")} {S(c, "captureSeq")} {S(c, "call")} [{string.Join(", ", c.GetProperty("notCompared").EnumerateArray().Select(x => x.GetString()))}]"
            + string.Concat(c.GetProperty("differences").EnumerateArray().Select(d => $" | {S(d, "level")} {S(d, "kind")} {S(d, "path")}: {S(d, "legacy")} -> {S(d, "new")}")));
        Assert.Equal(new[]
        {
            "1 1 Auth/Login [] | request changed Login/User: redacted:* -> redacted:* | request changed Login/Workstation: WS-<norm:workstation-number> -> XS-<norm:workstation-number>",
            "2 3 Policy/GetPolicy [Audit]",
            "3 2 Product/GetRiders []",
            "4 null Product/GetLimits [] | call missing null: null -> null",
            "5 4 Policy/SaveRider [] | request block missing SaveRider/Audit: SaveRider/Audit/Reason = \"client request\"; SaveRider/Audit/Channel = \"desk\" -> null | request changed SaveRider/Rider/EffectiveDate: 2024-01-01 -> 2026-11-03",
            "6 5 Policy/GetPolicyRiders [] | error changed error: null -> host timeout",
            "null 6 Audit/Ping [] | call extra null: null -> null",
        }, calls.Select(c => System.Text.RegularExpressions.Regex.Replace(c, "redacted:[0-9a-f]{8}", "redacted:*")));
        Assert.Equal(7, root.GetProperty("calls").EnumerateArray().Sum(c => c.GetProperty("differences").GetArrayLength()));    // every difference sits under one call
        var user = root.GetProperty("calls")[0].GetProperty("differences")[0];
        Assert.NotEqual(S(user, "legacy"), S(user, "new"));                                                    // two users, two pseudonyms
        var block = root.GetProperty("calls")[4].GetProperty("differences")[0];
        Assert.Equal(new[] { "SaveRider/Audit/Reason=client request", "SaveRider/Audit/Channel=desk" }, block.GetProperty("leaves").EnumerateArray().Select(l => $"{S(l, "path")}={S(l, "value")}"));
        Assert.Equal(0, root.GetProperty("calls")[4].GetProperty("differences")[1].GetProperty("leaves").GetArrayLength());
        Assert.Contains("the legacy client makes this call; the new client never does", S(root.GetProperty("calls")[3].GetProperty("differences")[0], "detail"));
        foreach (var raw in new[] { "agent1", "agent2", "WS-01", "XS-01", "Ada Lovelace", "S-0" }) Assert.DoesNotContain(raw, diff.Out);    // redacted and normalized as the report
        Assert.Equal("2 2 10", $"{S(root.GetProperty("rules"), "version")} {root.GetProperty("rules").GetProperty("masked").GetProperty("norm:workstation-number").GetInt32()} {root.GetProperty("rules").GetProperty("masked").GetProperty("norm:session-id").GetInt32()}");
        Assert.Equal(Recording.Load(Recording.RecordingDir(dir, Scn)).Seal, S(root.GetProperty("recording"), "seal"));
        Assert.Equal(Recording.Load(Recording.ReplayDir(dir, Scn)).Seal, S(root.GetProperty("replay"), "seal"));

        // the text report of the same replay is unchanged: one field's values quoted, a block's leaves, an absent error
        var report = Run(legacyDir, env, "oracle", "diff", "PolicyWizard", Scn);
        Assert.Equal(1, report.Code);
        Assert.StartsWith("scn:rider-added: 6 recorded call(s), 6 replayed, 7 difference(s)\n", report.Out);
        Assert.Contains("legacy: \"WS-<norm:workstation-number>\"\n   new: \"XS-<norm:workstation-number>\"", report.Out);
        Assert.Contains("legacy: SaveRider/Audit/Reason = \"client request\"; SaveRider/Audit/Channel = \"desk\"\n   new: (absent)", report.Out);
        Assert.Contains("legacy: \"(no error)\"\n   new: \"host timeout\"", report.Out);

        // after the fixes: exit 0, and every call is listed, each paired, with no difference (a consumer sets "none", not only "open");
        // --json may come before the positionals
        var fixedRun = Capture(Path.Combine(scratch, "fixed.jsonl"), c => { c.SkipsLimits = false; c.OmitsAudit = false; c.DatesFromClock = false; });
        var clean = Run(legacyDir, env, "oracle", "diff", "--json", "PolicyWizard", Scn, "--capture", fixedRun);
        Assert.True(clean.Code == 0, clean.Out + clean.Err);
        var cleanCalls = JsonDocument.Parse(clean.Out).RootElement.GetProperty("calls").EnumerateArray().ToList();
        Assert.Equal(new[] { "1 1", "2 3", "3 2", "4 4", "5 5", "6 6" }, cleanCalls.Select(c => $"{S(c, "recordedSeq")} {S(c, "captureSeq")}"));
        Assert.All(cleanCalls, c => Assert.Equal(0, c.GetProperty("differences").GetArrayLength()));
    }

    [Fact]
    public void issue_36_reads_reorder_between_writes_but_never_across_one_and_payloads_compare_by_field()
    {
        var root = Path.Combine(_f.OutRoot, "36unit");
        RecordingFile Side(string name, params (string Key, bool Write, string Request, string? Dataset)[] calls)
        {
            var dir = Path.Combine(root, name);
            Directory.CreateDirectory(Path.Combine(dir, "calls"));
            var list = new List<RecordedCall>();
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (c, i) in calls.Select((c, i) => (c, i + 1)))
            {
                var req = $"calls/{i:000}.request.{Payload.Format(c.Request)}";
                File.WriteAllText(Path.Combine(dir, req), c.Request);
                files[req] = Recording.Hash(File.ReadAllBytes(Path.Combine(dir, req)));
                string? ds = null;
                if (c.Dataset != null) { ds = $"calls/{i:000}.dataset.{Payload.Format(c.Dataset)}"; File.WriteAllText(Path.Combine(dir, ds), c.Dataset); files[ds] = Recording.Hash(File.ReadAllBytes(Path.Combine(dir, ds))); }
                var parts = c.Key.Split('/');
                list.Add(new RecordedCall(i, parts[0], parts[1], c.Write, req, null, ds, null, null));
            }
            var r = new RecordingFile(1, "recording", "scn:x", 1, new Provenance("2026-11-03", null, null, "t", new(), calls.Length, "test", Recording.Tool), 0, list, files, "");
            return r with { Seal = Recording.SealOf(r) };
        }
        DiffResult Compare(RecordingFile l, RecordingFile n) => OracleDiff.Compare(Path.Combine(root, "l"), l, Path.Combine(root, "n"), n, new NormSet());

        // reads swapped before the write: nothing; a read moved across the write: one reordered call
        var legacy = Side("l", ("A/Read1", false, "{\"id\":1}", null), ("A/Read2", false, "{\"id\":2}", null), ("A/Save", true, "{\"x\":1}", null), ("A/Read3", false, "{}", null));
        Assert.Empty(Compare(legacy, Side("n", ("A/Read2", false, "{\"id\":2}", null), ("A/Read1", false, "{\"id\":1}", null), ("A/Save", true, "{\"x\":1}", null), ("A/Read3", false, "{}", null))).Differences);
        var moved = Compare(legacy, Side("n", ("A/Read1", false, "{\"id\":1}", null), ("A/Save", true, "{\"x\":1}", null), ("A/Read2", false, "{\"id\":2}", null), ("A/Read3", false, "{}", null))).Differences;
        Assert.Equal("order reordered 2 A/Read2", Assert.Single(moved) is var d ? $"{d.Part} {d.Kind} {d.LegacySeq} {d.Call}" : "");

        // a repeated call skipped in the middle: the alignment pairs by content, so only the skipped one is missing
        var three = Side("l", ("B/Get", false, "{\"page\":1}", null), ("B/Get", false, "{\"page\":2}", null), ("B/Get", false, "{\"page\":3}", null));
        var two = Compare(three, Side("n", ("B/Get", false, "{\"page\":1}", null), ("B/Get", false, "{\"page\":3}", null))).Differences;
        Assert.Equal("call missing 2", Assert.Single(two) is var m ? $"{m.Part} {m.Kind} {m.LegacySeq}" : "");

        // JSON fields and arrays; a dataset compared per table and column, restricted to the new reply's tables
        var json = Compare(Side("l", ("C/Find", false, "{\"q\":{\"name\":\"a\",\"tags\":[\"x\",\"y\"]}}", "{\"T\":[{\"A\":1,\"B\":\"p\"},{\"A\":2,\"B\":\"q\"}],\"U\":[{\"Z\":1}]}")),
                           Side("n", ("C/Find", false, "{\"q\":{\"name\":\"b\",\"tags\":[\"x\"]},\"extra\":true}", "{\"T\":[{\"A\":1,\"B\":\"r\"},{\"A\":2,\"B\":\"s\"},{\"A\":3,\"B\":\"t\"}]}")));
        var lines = json.Differences.Select(x => $"{x.Part} {x.Kind} {x.Path} {x.Legacy}|{x.New}|{x.Detail}").ToList();
        Assert.Equal(new[]
        {
            "request extra extra |true|",
            "request changed q/name a|b|",
            "request missing q/tags[2] y||",
            "dataset changed T rows 2|3|",
            "dataset changed T[1]/B p|r|differs in 2 of 2 rows; the first is shown",
        }, lines);
        Assert.Equal("#1 C/Find: U", Assert.Single(json.NotCompared));

        // a capture without the dataset (or with no table in it) where the recording has one is a difference, not a pass
        foreach (var capture in new string?[] { null, "{}" })
        {
            var missing = Compare(Side("l", ("E/Find", false, "{}", "{\"T\":[{\"A\":1}],\"U\":[{\"Z\":1}]}")), Side("n", ("E/Find", false, "{}", capture)));
            Assert.Equal("dataset missing 1 E/Find tables T, U", Assert.Single(missing.Differences) is var nd ? $"{nd.Part} {nd.Kind} {nd.LegacySeq} {nd.Call} {nd.Legacy}" : "");
            Assert.Empty(missing.NotCompared);
        }

        // XML: one element on one side pairs with the first of several on the other; only the extra one differs, as one block
        var xml = Compare(Side("l", ("D/Put", false, "<Put><Item><Code>a</Code><Qty>1</Qty></Item><Item><Code>b</Code><Qty>2</Qty></Item></Put>", null)),
                          Side("n", ("D/Put", false, "<Put><Item><Code>a</Code><Qty>1</Qty></Item></Put>", null)));
        Assert.Equal("request missing Put/Item[2] 2", Assert.Single(xml.Differences) is var b ? $"{b.Part} {b.Kind} {b.Path} {b.Fields}" : "");
    }

    [Fact]
    public void issue_36_a_dataset_that_is_not_a_serialized_dataset_is_refused_at_export_and_at_the_replay_import_with_the_call_named()
    {
        var dir = Module("36ds");
        var log = Path.Combine(_f.OutRoot, "copies", "36ds", "bad.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, "{\"time\":\"2026-11-03T09:00:00Z\",\"session\":\"s1\",\"user\":\"u\",\"category\":\"Product\",\"type\":\"GetRiders\",\"request\":\"<GetRiders/>\",\"dataset\":\"plain text\"}\n");
        var none = new OracleConfig();
        var export = new ExportRequest(dir, Scn, "recording", new() { log }, new() { "s1" }, null, null, "alex", null, null, false, none, null, Today);
        Phase(dir, "record");
        Assert.Contains("row 1 of the selection (Product/GetRiders)", Assert.Throws<InvalidOperationException>(() => OracleModule.Export(export)).Message);
        Assert.False(Directory.Exists(Recording.RecordingDir(dir, Scn)));
        Assert.Contains("row 1 of the selection (Product/GetRiders)", Assert.Throws<InvalidOperationException>(() => OracleModule.Import(dir, Scn, log, none, null, Today)).Message);
        Assert.False(Directory.Exists(Recording.ReplayDir(dir, Scn)));
    }

    [Fact]
    public void issue_36_normalization_rules_need_no_version_but_a_malformed_one_is_refused()
    {
        var dir = Module("36ver");
        Directory.CreateDirectory(Path.Combine(dir, "recordings"));
        var rule = "{ \"id\": \"norm:session-id\", \"decision\": \"dec:summary-needs-a-beneficiary\", \"scope\": \"global\", \"target\": \"any\", \"field\": \"**/SessionId\" }";
        var decisions = Registry.Decisions(dir).Select(d => d with { Cite = d.Cite.Append("norm:session-id").ToList() }).ToList();
        File.WriteAllText(Normalization.PathOf(dir), "{ \"rules\": [ " + rule + " ] }");
        var (set, findings) = Normalization.Load(dir, decisions);
        Assert.Empty(findings);
        Assert.Equal(0, set.Version);
        Assert.Single(set.Rules);
        File.WriteAllText(Normalization.PathOf(dir), "{ \"version\": 7, \"rules\": [ " + rule + " ] }");
        Assert.Equal(7, Normalization.Load(dir, decisions).Set.Version);
        File.WriteAllText(Normalization.PathOf(dir), "{ \"version\": \"two\", \"rules\": [ " + rule + " ] }");
        Assert.Contains(Normalization.Load(dir, decisions).Findings, x => x.Code == "NORM-INVALID" && x.Message.Contains("version"));
    }

    /// <summary>
    /// Issue #39: a consuming repo with a recording and a new-client capture but no map-phase registry (no extracted manifest) cannot
    /// run the registry diff; the standalone diff (<c>--recording</c>, <c>--capture</c>) compares them with no registry read, with the
    /// recording's own redaction and normalization, the same exit codes, and nothing written.
    /// </summary>
    [Fact]
    public void issue_39_the_standalone_diff_compares_a_recording_and_a_capture_with_no_registry_and_writes_nothing()
    {
        // a recording made where a registry exists, with its settings and a decided normalization rule beside it
        var dir = Module("39");
        var scratch = Path.Combine(_f.OutRoot, "copies", "39", "scratch");
        Directory.CreateDirectory(scratch);
        var (log, session) = RecordLegacy(scratch);
        var cfg = Config();
        Phase(dir, "record");
        WriteSettings(dir, cfg);
        File.WriteAllText(Normalization.PathOf(dir), """
        { "rules": [ { "id": "norm:session-id", "decision": "dec:summary-needs-a-beneficiary", "scope": "global", "target": "any", "field": "**/SessionId" } ] }
        """);
        var decisions = Path.Combine(dir, "registry", "decisions.json");
        File.WriteAllText(decisions, File.ReadAllText(decisions).Replace("\"cite\": [\"handler:CoverSummaryForm/btnConfirm_Click\"]", "\"cite\": [\"handler:CoverSummaryForm/btnConfirm_Click\", \"norm:session-id\"]"));
        OracleModule.Export(new ExportRequest(dir, Scn, "recording", new() { log }, new() { session }, null, null, "alex", "local", "4.2.1", false, cfg, Key, Today));
        var seeded = Capture(Path.Combine(scratch, "seeded.jsonl"), _ => { });
        var fixedRun = Capture(Path.Combine(scratch, "fixed.jsonl"), c => { c.SkipsLimits = false; c.OmitsAudit = false; c.DatesFromClock = false; });

        // the consuming repo: the recordings folder only, no registry, no module.json, no manifests
        var consumer = Path.Combine(_f.OutRoot, "copies", "39consumer", "legacy", "PolicyWizard");
        foreach (var f in Directory.EnumerateFiles(Path.Combine(dir, "recordings"), "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(consumer, Path.GetRelativePath(dir, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        var recording = Path.Combine(consumer, "recordings", "rider-added");
        var cwd = Path.GetDirectoryName(consumer)!;
        var env = new Dictionary<string, string?> { ["PARITY_REDACT_KEY"] = Key };
        string[] Files() => Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(cwd)!, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        var before = Files();

        // the bug: the registry diff refuses the scenario, since the map phase never wrote one
        var registry = Run(cwd, env, "oracle", "diff", "PolicyWizard", Scn, "--capture", seeded);
        Assert.Equal(3, registry.Code);
        Assert.Contains("registry/scenarios.json", registry.Err);

        // standalone: the seeded differences, the session id masked by the recording's own rule, the redaction applied to the capture
        var diff = Run(cwd, env, "oracle", "diff", "--recording", recording, "--capture", seeded);
        Assert.True(diff.Code == 1, diff.Out + diff.Err);
        Assert.StartsWith("scn:rider-added: 6 recorded call(s), 5 replayed, 3 difference(s)\n", diff.Out);
        Assert.Contains(OracleModule.StandaloneNote + "\n", diff.Out);
        Assert.Contains("(masked: norm:session-id 10)", diff.Out);
        Assert.Contains("1. missing call: #4 Product/GetLimits", diff.Out);
        Assert.Contains("2. request block missing: #5 Policy/SaveRider SaveRider/Audit (2 fields)", diff.Out);
        Assert.DoesNotContain("legacy client:", diff.Out);                                                       // no registry, so no wire mapping to point at
        Assert.DoesNotContain("agent1", diff.Out);                                                               // the capture went through the redaction
        var green = Run(cwd, env, "oracle", "diff", "--recording", recording, "--capture", fixedRun);
        Assert.True(green.Code == 0, green.Out + green.Err);
        Assert.StartsWith("scn:rider-added: 6 recorded call(s), 6 replayed, 0 difference(s)\n", green.Out);
        Assert.Equal(before, Files());                                                                           // no replays/, no verified/, no registry edit

        // a sealed replay folder is a capture too: the registry diff's own import of the same rows gives the same report
        Phase(dir, "implement");
        OracleModule.Diff(dir, Scn, seeded, cfg, Key, Today);
        var fromDir = Run(cwd, env, "oracle", "diff", "--recording", recording, "--capture", Recording.ReplayDir(dir, Scn));
        Assert.Equal(1, fromDir.Code);
        Assert.Equal(diff.Out, fromDir.Out);
        var asRecording = Run(cwd, env, "oracle", "diff", "--recording", Recording.ReplayDir(dir, Scn), "--capture", seeded);
        Assert.Equal(3, asRecording.Code);
        Assert.Contains("holds a replay, not a recording", asRecording.Err);
        var recordingAsCapture = Run(cwd, env, "oracle", "diff", "--recording", recording, "--capture", recording);
        Assert.Equal(3, recordingAsCapture.Code);
        Assert.Contains("--capture takes a replay of scn:rider-added", recordingAsCapture.Err);

        // refused as the registry diff refuses: no redaction key for the recording's rules, a broken seal, an invalid rule
        Assert.Contains("PARITY_REDACT_KEY", Run(cwd, new Dictionary<string, string?> { ["PARITY_REDACT_KEY"] = null }, "oracle", "diff", "--recording", recording, "--capture", seeded) is { Code: 3 } k ? k.Err : "exit was not 3");
        var request = Path.Combine(recording, "calls", "005.request.xml");
        var original = File.ReadAllText(request);
        File.WriteAllText(request, original.Replace("<Channel>desk</Channel>", ""));
        var tampered = Run(cwd, env, "oracle", "diff", "--recording", recording, "--capture", seeded);
        Assert.Equal(3, tampered.Code);
        Assert.Contains("no longer matches its seal (calls/005.request.xml was edited after export)", tampered.Err);
        File.WriteAllText(request, original);
        var rules = Path.Combine(consumer, "recordings", Normalization.FileName);
        var rulesText = File.ReadAllText(rules);
        File.WriteAllText(rules, rulesText.Replace("\"scope\": \"global\"", "\"scope\": \"everywhere\""));
        var invalid = Run(cwd, env, "oracle", "diff", "--recording", recording, "--capture", seeded);
        Assert.Equal(3, invalid.Code);
        Assert.Contains("NORM-INVALID", invalid.Err);
        File.WriteAllText(rules, rulesText);

        // usage: one form or the other, and the standalone form needs both sides
        Assert.Equal(2, Run(cwd, env, "oracle", "diff", "PolicyWizard", Scn, "--recording", recording, "--capture", seeded).Code);
        Assert.Equal(2, Run(cwd, env, "oracle", "diff", "--recording", recording).Code);
        Assert.Equal(3, Run(cwd, env, "oracle", "diff", "--recording", recording, "--capture", Path.Combine(scratch, "absent.jsonl")).Code);
    }

    private (int Code, string Out, string Err) Run(string cwd, IDictionary<string, string?> env, params string[] args)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet") { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "parity.dll"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) { if (v == null) psi.Environment.Remove(k); else psi.Environment[k] = v; }
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o, err.Result);
    }
}
