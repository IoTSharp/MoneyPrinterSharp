using System.Collections.Immutable;
using System.Diagnostics;

namespace VideoProduction;

/// <summary>项目外发引用类型；源码按行授权，其他引用只允许完整的单个项目对象。</summary>
public enum OutboundDataKind { Asset, Source, Text }

/// <summary>不可变的逻辑引用；路径是项目内标识，尚不代表已绑定的本地文件句柄。</summary>
public sealed record OutboundDataReference(OutboundDataKind Kind, string ProjectPath, int? StartLine = null, int? EndLine = null);

/// <summary>阶段 A 的项目外发授权契约；任何维度缺失或不匹配均拒绝。</summary>
public sealed record ProjectOutboundAuthorization(
    string Id, string ProjectId, string Provider, string AccountAlias, string Model,
    string Capability, string Purpose, decimal BudgetCny, DateTimeOffset ExpiresAt,
    ImmutableArray<OutboundDataReference> Scopes);

/// <summary>发送前的不可变意图；累计占用由可信账本适配器提供，不能采信代理自报。</summary>
public sealed record ProjectOutboundIntent(
    string ProjectId, string Provider, string AccountAlias, string Model,
    string Capability, string Purpose, decimal CommittedCny, decimal EstimateCny,
    ImmutableArray<OutboundDataReference> References);

/// <summary>由宿主注入的发送适配器；未来实现须绑定引用与实际字节，并遵守取消令牌。</summary>
public interface IProjectOutboundTransport
{
    /// <summary>接收已经通过契约校验的意图，不得自行扩展目的地或素材范围。</summary>
    Task SendAsync(ProjectOutboundIntent intent, CancellationToken ct);
}

/// <summary>离线可验证的默认拒绝边界；尚未接管现有 CLI 的实际 HTTP 请求。</summary>
public static class ProjectOutboundGate
{
    private const int ReferenceLimit = 64;

    /// <summary>使用宿主当前 UTC 时间校验授权；调用者不能通过意图指定过期检查时间。</summary>
    public static async Task SendAsync(ProjectOutboundAuthorization? authorization, ProjectOutboundIntent intent,
        IProjectOutboundTransport transport, CancellationToken ct) =>
        await SendAsync(authorization, intent, transport, DateTimeOffset.UtcNow, ct);

    /// <summary>离线测试可注入时刻；最多64×64次范围比较和5秒校验，拒绝或预先取消时零发送。</summary>
    internal static async Task SendAsync(ProjectOutboundAuthorization? authorization, ProjectOutboundIntent intent,
        IProjectOutboundTransport transport, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(transport);
        ct.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        if (authorization is null) throw Denied();
        if (!Token(authorization.Id) || !Token(intent.ProjectId) || !Token(intent.Provider) || !Token(intent.AccountAlias) ||
            !Token(intent.Model) || !Token(intent.Capability) || !Token(intent.Purpose) ||
            authorization.ProjectId != intent.ProjectId || authorization.Provider != intent.Provider ||
            authorization.AccountAlias != intent.AccountAlias || authorization.Model != intent.Model ||
            authorization.Capability != intent.Capability || authorization.Purpose != intent.Purpose ||
            authorization.ExpiresAt <= now || authorization.BudgetCny is <= 0 or > 1000000 ||
            intent.CommittedCny is < 0 or > 1000000 || intent.EstimateCny is < 0 or > 1000000 ||
            intent.CommittedCny + intent.EstimateCny > authorization.BudgetCny ||
            authorization.Scopes.IsDefaultOrEmpty || authorization.Scopes.Length > ReferenceLimit ||
            intent.References.IsDefaultOrEmpty || intent.References.Length > ReferenceLimit) throw Denied();

        // 先校验全部授权和引用，避免无效授权项因短路匹配而被忽略。
        foreach (var scope in authorization.Scopes) ValidateReference(scope, timer, ct);
        foreach (var reference in intent.References)
        {
            ValidateReference(reference, timer, ct);
            var allowed = false;
            foreach (var scope in authorization.Scopes)
            {
                CheckLimit(timer, ct);
                if (scope.Kind == reference.Kind && scope.ProjectPath == reference.ProjectPath &&
                    (reference.Kind != OutboundDataKind.Source ||
                     reference.StartLine >= scope.StartLine && reference.EndLine <= scope.EndLine)) allowed = true;
            }
            if (!allowed) throw Denied();
        }
        CheckLimit(timer, ct);
        // 扣除校验耗时，发送等待也不能越过剩余授权时间。
        var remaining = authorization.ExpiresAt - now - timer.Elapsed;
        if (remaining <= TimeSpan.Zero) throw Denied();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(remaining < TimeSpan.FromMinutes(3) ? remaining : TimeSpan.FromMinutes(3));
        deadline.Token.ThrowIfCancellationRequested();
        await transport.SendAsync(intent, deadline.Token).WaitAsync(deadline.Token);
    }

    /// <summary>标识符长度限制为128且仅使用安全字符，拒绝原文、路径和控制字符混入。</summary>
    private static bool Token(string value) => !string.IsNullOrEmpty(value) && value.Length <= 128 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>限定项目逻辑路径及源码行号；不解析磁盘、符号链接或重解析点。</summary>
    private static void ValidateReference(OutboundDataReference reference, Stopwatch timer, CancellationToken ct)
    {
        CheckLimit(timer, ct);
        if (reference is null || !Enum.IsDefined(reference.Kind) || string.IsNullOrEmpty(reference.ProjectPath) ||
            reference.ProjectPath.Length > 1024 || reference.ProjectPath[0] == '/' ||
            reference.ProjectPath.IndexOfAny(['\\', ':', '?', '*', '"', '<', '>', '|', '%']) >= 0 ||
            reference.ProjectPath.Any(char.IsControl)) throw Denied();
        var parts = reference.ProjectPath.Split('/');
        if (parts.Length > 32) throw Denied();
        foreach (var part in parts)
        {
            CheckLimit(timer, ct);
            if (part.Length is < 1 or > 128 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')) throw Denied();
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                stem[3] is >= '0' and <= '9') throw Denied();
        }
        if (reference.Kind == OutboundDataKind.Source)
        {
            if (reference.StartLine is null or < 1 or > 1000000 || reference.EndLine is null or < 1 or > 1000000 ||
                reference.EndLine < reference.StartLine) throw Denied();
        }
        else if (reference.StartLine is not null || reference.EndLine is not null) throw Denied();
    }

    /// <summary>所有范围和路径循环共享5秒墙钟上限并响应调用方取消。</summary>
    private static void CheckLimit(Stopwatch timer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("项目外发授权校验超时。");
    }

    /// <summary>拒绝日志仅给出固定错误，不回显路径、提示词或账号输入。</summary>
    private static UnauthorizedAccessException Denied() => new("项目外发意图不在有效授权范围内。");
}
