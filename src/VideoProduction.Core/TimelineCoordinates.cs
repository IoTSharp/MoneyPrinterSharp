using System.Globalization;
using System.Numerics;

namespace VideoProduction;

/// <summary>离散坐标转换时采用的明确舍入方式；Nearest 的半值向上。</summary>
public enum TimeRounding { Floor, Nearest, Ceiling }

/// <summary>以约分后的有理数秒表示非负时间；默认值为零，最长十二小时。</summary>
public readonly struct RationalTime : IEquatable<RationalTime>, IComparable<RationalTime>
{
    public const long MaxSeconds = 12 * 60 * 60;
    private readonly long numerator;
    private readonly long denominator;

    public long Numerator => numerator;
    public long Denominator => denominator == 0 ? 1 : denominator;

    /// <summary>创建精确时间；不能精确存入 Int64 或超出十二小时则拒绝。</summary>
    public RationalTime(long numerator, long denominator)
        : this((BigInteger)numerator, (BigInteger)denominator) { }

    /// <summary>用大整数先检查范围并约分，最后才转换为 Int64。</summary>
    private RationalTime(BigInteger numerator, BigInteger denominator)
    {
        if (numerator < 0 || denominator <= 0 || numerator > MaxSeconds * denominator)
            throw new ArgumentOutOfRangeException(nameof(numerator), "时间须处于零到十二小时之间，分母须为正数。");
        var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
        numerator /= divisor;
        denominator /= divisor;
        if (numerator > long.MaxValue || denominator > long.MaxValue)
            throw new OverflowException("有理数时间无法精确存入 Int64。");
        this.numerator = (long)numerator;
        this.denominator = (long)denominator;
    }

    /// <summary>为跨分母计算创建不会静默舍入的精确时间。</summary>
    internal static RationalTime Exact(BigInteger numerator, BigInteger denominator) => new(numerator, denominator);

    /// <summary>从音频采样序号构造时间，不对单个采样间隔累计舍入。</summary>
    public static RationalTime FromAudioSamples(long sampleIndex, int sampleRate)
    {
        if (sampleRate is < 1 or > 384_000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        return new RationalTime(sampleIndex, sampleRate);
    }

    /// <summary>将绝对时间一次转换为采样序号。</summary>
    public long ToAudioSamples(int sampleRate, TimeRounding rounding)
    {
        if (sampleRate is < 1 or > 384_000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        return Round((BigInteger)Numerator * sampleRate, Denominator, rounding);
    }

    /// <summary>精确相加，结果超出表示范围时明确失败。</summary>
    public RationalTime Add(RationalTime other) => Exact(
        (BigInteger)Numerator * other.Denominator + (BigInteger)other.Numerator * Denominator,
        (BigInteger)Denominator * other.Denominator);

    /// <summary>精确相减，不允许负时间。</summary>
    public RationalTime Subtract(RationalTime other) => Exact(
        (BigInteger)Numerator * other.Denominator - (BigInteger)other.Numerator * Denominator,
        (BigInteger)Denominator * other.Denominator);

    /// <summary>按正有理数缩放，用于片段变速与源时间映射。</summary>
    public RationalTime Scale(long factorNumerator, long factorDenominator)
    {
        if (factorNumerator <= 0 || factorDenominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(factorNumerator), "缩放比须为正数。");
        return Exact((BigInteger)Numerator * factorNumerator, (BigInteger)Denominator * factorDenominator);
    }

    /// <summary>交叉相乘比较，避免先转浮点数。</summary>
    public int CompareTo(RationalTime other) =>
        ((BigInteger)Numerator * other.Denominator).CompareTo((BigInteger)other.Numerator * Denominator);

    /// <summary>按约分后的分子分母比较精确值。</summary>
    public bool Equals(RationalTime other) => Numerator == other.Numerator && Denominator == other.Denominator;
    /// <summary>只接受相同时间值的对象。</summary>
    public override bool Equals(object? obj) => obj is RationalTime other && Equals(other);
    /// <summary>由规范化分数生成稳定哈希。</summary>
    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);
    /// <summary>以不依赖区域设置的分数显示时间。</summary>
    public override string ToString() => $"{Numerator.ToString(CultureInfo.InvariantCulture)}/{Denominator.ToString(CultureInfo.InvariantCulture)}";
    /// <summary>比较两个精确时间是否相等。</summary>
    public static bool operator ==(RationalTime left, RationalTime right) => left.Equals(right);
    /// <summary>比较两个精确时间是否不等。</summary>
    public static bool operator !=(RationalTime left, RationalTime right) => !left.Equals(right);
    /// <summary>按真实时间顺序比较大小。</summary>
    public static bool operator <(RationalTime left, RationalTime right) => left.CompareTo(right) < 0;
    /// <summary>按真实时间顺序比较大小或相等。</summary>
    public static bool operator <=(RationalTime left, RationalTime right) => left.CompareTo(right) <= 0;
    /// <summary>按真实时间顺序比较大小。</summary>
    public static bool operator >(RationalTime left, RationalTime right) => left.CompareTo(right) > 0;
    /// <summary>按真实时间顺序比较大小或相等。</summary>
    public static bool operator >=(RationalTime left, RationalTime right) => left.CompareTo(right) >= 0;

    /// <summary>只在离散坐标边界舍入，半值向上；调用方不得逐帧累计结果。</summary>
    internal static long Round(BigInteger numerator, BigInteger denominator, TimeRounding rounding)
    {
        if (numerator < 0 || denominator <= 0) throw new ArgumentOutOfRangeException(nameof(numerator));
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        quotient += rounding switch
        {
            TimeRounding.Floor => 0,
            TimeRounding.Ceiling when remainder > 0 => 1,
            TimeRounding.Ceiling => 0,
            TimeRounding.Nearest when remainder * 2 >= denominator => 1,
            TimeRounding.Nearest => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(rounding))
        };
        if (quotient > long.MaxValue) throw new OverflowException("离散坐标超出 Int64。");
        return (long)quotient;
    }
}

/// <summary>源素材中的时间坐标；与项目时间保持类型区分。</summary>
public readonly record struct SourceTime(RationalTime Value)
{
    /// <summary>按源时间基把绝对 PTS 减去首帧 PTS，允许容器从负 PTS 起始。</summary>
    public static SourceTime FromPts(long pts, long firstPts, int timeBaseNumerator, int timeBaseDenominator)
    {
        if (timeBaseNumerator <= 0 || timeBaseDenominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeBaseNumerator), "源时间基须为正数。");
        return new SourceTime(RationalTime.Exact(
            ((BigInteger)pts - firstPts) * timeBaseNumerator, timeBaseDenominator));
    }

    /// <summary>以采样序号定位源音频，不经浮点秒数。</summary>
    public static SourceTime FromAudioSamples(long sampleIndex, int sampleRate) =>
        new(RationalTime.FromAudioSamples(sampleIndex, sampleRate));
}

/// <summary>项目时间线中的时间坐标。</summary>
public readonly record struct ProjectTime(RationalTime Value)
{
    /// <summary>以采样序号定位项目音频，不经浮点秒数。</summary>
    public static ProjectTime FromAudioSamples(long sampleIndex, int sampleRate) =>
        new(RationalTime.FromAudioSamples(sampleIndex, sampleRate));
}

/// <summary>每秒帧数的有理数表示，支持整数及 30000/1001 等帧率。</summary>
public readonly struct FrameRate
{
    public int Numerator { get; }
    public int Denominator { get; }

    /// <summary>接受每秒 1 至 240 帧、分母不超过一百万的帧率。</summary>
    public FrameRate(int numerator, int denominator)
    {
        if (denominator is < 1 or > 1_000_000 || numerator < denominator ||
            (long)numerator > 240L * denominator)
            throw new ArgumentOutOfRangeException(nameof(numerator), "帧率须处于 1 至 240 fps。");
        var divisor = (int)BigInteger.GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / divisor;
        Denominator = denominator / divisor;
    }

    /// <summary>从帧序号计算精确项目时间，帧序号零表示起点。</summary>
    public ProjectTime FrameStart(long frameIndex)
    {
        EnsureValid();
        return new ProjectTime(RationalTime.Exact((BigInteger)frameIndex * Denominator, Numerator));
    }

    /// <summary>从绝对项目时间一次换算帧序号，舍入方式由调用方指定。</summary>
    public long FrameAt(ProjectTime time, TimeRounding rounding)
    {
        EnsureValid();
        return RationalTime.Round((BigInteger)time.Value.Numerator * Numerator,
            (BigInteger)time.Value.Denominator * Denominator, rounding);
    }

    /// <summary>整数帧率显示为非丢帧 HH:MM:SS:FF；非整数帧率需另选时间码规则。</summary>
    public string FormatTimecode(long frameIndex)
    {
        EnsureValid();
        if (Denominator != 1) throw new NotSupportedException("非整数帧率须明确选择丢帧或非丢帧时间码规则。");
        _ = FrameStart(frameIndex);
        var seconds = frameIndex / Numerator;
        var frames = frameIndex % Numerator;
        return string.Create(CultureInfo.InvariantCulture,
            $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}:{frames:00}");
    }

    /// <summary>拒绝未通过构造函数初始化的默认帧率值。</summary>
    private void EnsureValid()
    {
        if (Numerator <= 0 || Denominator <= 0) throw new InvalidOperationException("帧率未初始化。");
    }
}

/// <summary>片段的源时间到项目时间映射；入点、出点与项目起点均为边界坐标。</summary>
public sealed class ClipTimeMapping
{
    public SourceTime SourceIn { get; }
    public SourceTime SourceOut { get; }
    public ProjectTime TimelineStart { get; }
    public ProjectTime TimelineEnd { get; }
    public long SpeedNumerator { get; }
    public long SpeedDenominator { get; }

    /// <summary>构造有界变速片段；速度为源秒数/项目秒数，范围 0.01 至 100。</summary>
    public ClipTimeMapping(SourceTime sourceIn, SourceTime sourceOut, ProjectTime timelineStart,
        long speedNumerator = 1, long speedDenominator = 1)
    {
        if (sourceOut.Value <= sourceIn.Value || speedNumerator is < 1 or > 1_000_000 ||
            speedDenominator is < 1 or > 1_000_000 ||
            (BigInteger)speedNumerator * 100 < speedDenominator ||
            (BigInteger)speedNumerator > (BigInteger)speedDenominator * 100)
            throw new ArgumentOutOfRangeException(nameof(sourceOut), "片段范围或速度无效。");
        SourceIn = sourceIn;
        SourceOut = sourceOut;
        TimelineStart = timelineStart;
        SpeedNumerator = speedNumerator;
        SpeedDenominator = speedDenominator;
        TimelineEnd = new ProjectTime(timelineStart.Value.Add(
            sourceOut.Value.Subtract(sourceIn.Value).Scale(speedDenominator, speedNumerator)));
    }

    /// <summary>由绝对源时间映射项目时间，不逐帧累积舍入。</summary>
    public ProjectTime Map(SourceTime source)
    {
        if (source.Value < SourceIn.Value || source.Value > SourceOut.Value)
            throw new ArgumentOutOfRangeException(nameof(source), "源时间不在片段裁切范围内。");
        return new ProjectTime(TimelineStart.Value.Add(
            source.Value.Subtract(SourceIn.Value).Scale(SpeedDenominator, SpeedNumerator)));
    }

    /// <summary>直接从源 PTS 映射；首帧 PTS 与源时间基由素材索引提供。</summary>
    public ProjectTime MapPts(long pts, long firstPts, int timeBaseNumerator, int timeBaseDenominator) =>
        Map(SourceTime.FromPts(pts, firstPts, timeBaseNumerator, timeBaseDenominator));

    /// <summary>从项目时间反查源时间，预览定位与渲染共享同一逆映射。</summary>
    public SourceTime SourceAt(ProjectTime project)
    {
        if (project.Value < TimelineStart.Value || project.Value > TimelineEnd.Value)
            throw new ArgumentOutOfRangeException(nameof(project), "项目时间不在片段范围内。");
        return new SourceTime(SourceIn.Value.Add(
            project.Value.Subtract(TimelineStart.Value).Scale(SpeedNumerator, SpeedDenominator)));
    }
}
