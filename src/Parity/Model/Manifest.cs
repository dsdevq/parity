namespace Parity.Model;

/// <summary>Where a manifest item lives in the legacy source. The citation target.</summary>
public sealed record Loc(string File, int Start, int End);

public sealed record ControlInfo(
    string Id,
    string Field,
    string Type,
    string Category,
    string? Parent,
    string? LayoutItem,
    string? Caption,
    SortedDictionary<string, string> Props,
    List<string> Bindings,
    SortedDictionary<string, string> Events,
    Loc? Loc);

public sealed record EventWiring(string Id, string Control, string Event, string Handler, string Source, Loc Loc);

public sealed record BindingInfo(string Id, string Control, string Property, string DataSource, string Member, string Source, Loc Loc);

public sealed record ValidationRuleInfo(string Id, string Control, string Rule, string Source, Loc Loc);

public sealed record HandlerInfo(
    string Id,
    string Name,
    string Kind,
    List<string> Events,
    List<string> Reads,
    List<string> Writes,
    List<string> Calls,
    List<string> ServiceCalls,
    List<string> Unresolved,
    Loc Loc,
    string Source);

public sealed record MethodInfo(
    string Id,
    string Display,
    int Depth,
    List<string> Reads,
    List<string> Writes,
    List<string> Calls,
    List<string> ServiceCalls,
    List<string> Unresolved,
    Loc? Loc,
    string? Source,
    string? Assembly);

public sealed record ServiceCallInfo(string Id, string Type, string Method, List<string> CalledFrom, List<string> Args);

public sealed record DynamicUiInfo(string Id, string Type, string? AddedTo, string Handler, Loc Loc, string Statement);

/// <summary>
/// A static fact a slice or a bound model reads by name: a const or static readonly literal, or a resource text.
/// Not a citable id; rules keep citing the handler or method that uses it.
/// </summary>
public sealed record ConstantInfo(
    string Kind,
    string Type,
    string Member,
    string ValueType,
    string? Value,
    Loc? Declared,
    List<string> Cultures,
    Loc? FirstUse,
    List<string> UsedBy);

public sealed record RawStatement(string Statement, Loc Loc);

public sealed record Diagnostic(string Code, string Message, Loc? Loc);

public sealed record FormManifest(
    string SchemaVersion,
    string Id,
    string ClassName,
    string Namespace,
    List<string> Bases,
    List<string> Files,
    string? Caption,
    List<ControlInfo> Controls,
    List<EventWiring> Events,
    List<BindingInfo> Bindings,
    List<ValidationRuleInfo> ValidationRules,
    List<HandlerInfo> Handlers,
    List<MethodInfo> Methods,
    List<ServiceCallInfo> ServiceCalls,
    List<DynamicUiInfo> DynamicUi,
    List<ConstantInfo> Constants,
    List<RawStatement> Raw,
    List<Diagnostic> Diagnostics,
    List<VariantInfo>? Variants = null,
    List<FactoryInfo>? Factories = null);

/// <summary>One property a variant sets differently from the base unit. Base is null when the base never sets it.</summary>
public sealed record VariantChange(string Property, string? Base, string? Value, Loc? Loc, string? Control = null);

/// <summary>
/// One difference between a variant and its base unit, keyed var:&lt;Variant&gt;/&lt;kind&gt;/&lt;name&gt;.
/// Kinds: added, removed, retagged (controls); event (a wiring the variant adds); override, handler, ctor (code);
/// service-added, service-removed (the service boundary). Target names what the entry changes in the base.
/// </summary>
public sealed record VariantDelta(
    string Id,
    string Kind,
    string? Target,
    string? Detail,
    string? Type,
    string? Category,
    List<VariantChange>? Changes,
    List<string>? Events,
    List<string>? Reads,
    List<string>? Writes,
    List<string>? Calls,
    List<string>? ServiceCalls,
    string? Source,
    Loc? Loc);

/// <summary>A tenant or customer variant of the unit: a subclass, possibly in another project, described as a delta on the base.</summary>
public sealed record VariantInfo(
    string Id,
    string Name,
    string? Project,
    string Class,
    List<string> Bases,
    List<string> Files,
    List<VariantDelta> Delta,
    List<VariantChange> Layout,
    List<MethodInfo> Methods,
    List<RawStatement> Raw,
    List<Diagnostic> Diagnostics);

/// <summary>Which class a factory hands out for which key. Variant is "base" for the unit itself, the variant name, or null for another class.</summary>
public sealed record FactoryCase(string Key, string Class, string? Variant, Loc Loc);

public sealed record FactoryInfo(string Id, string On, List<FactoryCase> Cases, Loc Loc);

public sealed record ModuleIndex(
    string SchemaVersion,
    string Module,
    string Project,
    List<string> Forms,
    List<string> ServiceCalls,
    List<DuplicateCluster> Duplicates);

public sealed record DuplicateCluster(string Hash, string Kind, int Tokens, List<string> Members);
