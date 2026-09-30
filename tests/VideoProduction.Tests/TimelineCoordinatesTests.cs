using VideoProduction;

namespace VideoProductionTests;

/// <summary>时间坐标的纯本地回归，不依赖媒体文件或网络。</summary>
public static class TimelineCoordinatesTests
{
    /// <summary>执行有界的帧率、采样、裁切及长序列 VFR 断言。</summary>
    public static void Run(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        FramesAndTimecode(deadline.Token);
        AudioSamplesAndRounding(deadline.Token);
        TrimAndSpeed(deadline.Token);
        VariableFrameRateDoesNotAccumulateDrift(deadline.Token);
        InvalidAndOverflowValues(deadline.Token);
    }

    /// <summary>24、25、30、60 fps 的整秒边界与非丢帧显示均应精确。</summary>
    private static void FramesAndTimecode(CancellationToken cancellationToken)
    {
        var rates = new[] { 24, 25, 30, 60 };
        for (var index = 0; index < rates.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rate = new FrameRate(rates[index], 1);
            var second = rate.FrameStart(rates[index]);
            Assert(second.Value == new RationalTime(1, 1), $"{rates[index]} fps 的整秒有漂移");
            Assert(rate.FrameAt(second, TimeRounding.Floor) == rates[index], "整秒帧序号错误");
            Assert(rate.FormatTimecode(rates[index]) == "00:00:01:00", "整秒时间码错误");
            Assert(rate.FrameAt(new ProjectTime(new RationalTime(1, rates[index] * 2)), TimeRounding.Floor) == 0,
                "半帧向下舍入错误");
            Assert(rate.FrameAt(new ProjectTime(new RationalTime(1, rates[index] * 2)), TimeRounding.Nearest) == 1,
                "半帧最近舍入错误");
        }
        Assert(new FrameRate(24, 1).FormatTimecode(24 * 3661 + 23) == "01:01:01:23", "长时间码错误");
        Assert(new FrameRate(30000, 1001).FrameStart(30000).Value == new RationalTime(1001, 1),
            "分数帧率失去精度");
        Throws<NotSupportedException>(() => new FrameRate(30000, 1001).FormatTimecode(30),
            "分数帧率不能暗用整数非丢帧时间码");
    }

    /// <summary>音频采样在时间轴上保持整数坐标，半采样统一采用显式舍入。</summary>
    private static void AudioSamplesAndRounding(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var oneSecond = SourceTime.FromAudioSamples(48_000, 48_000);
        Assert(oneSecond.Value == new RationalTime(1, 1), "48 kHz 采样时间错误");
        Assert(ProjectTime.FromAudioSamples(44_100, 44_100).Value == oneSecond.Value,
            "源时间与项目时间的采样换算不一致");
        var halfSample = new RationalTime(1, 96_000);
        Assert(halfSample.ToAudioSamples(48_000, TimeRounding.Floor) == 0, "半采样向下舍入错误");
        Assert(halfSample.ToAudioSamples(48_000, TimeRounding.Nearest) == 1, "半采样最近舍入错误");
        Assert(halfSample.ToAudioSamples(48_000, TimeRounding.Ceiling) == 1, "半采样向上舍入错误");
    }

    /// <summary>源入点、出点、非零项目起点和变速都使用同一可逆映射。</summary>
    private static void TrimAndSpeed(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mapping = new ClipTimeMapping(new SourceTime(new RationalTime(1, 1)),
            new SourceTime(new RationalTime(5, 1)), new ProjectTime(new RationalTime(10, 1)), 2, 1);
        Assert(mapping.TimelineEnd.Value == new RationalTime(12, 1), "变速片段终点错误");
        Assert(mapping.Map(new SourceTime(new RationalTime(3, 1))).Value == new RationalTime(11, 1),
            "源时间到项目时间错误");
        Assert(mapping.SourceAt(new ProjectTime(new RationalTime(11, 1))).Value == new RationalTime(3, 1),
            "项目时间反查源时间错误");
        Assert(mapping.Map(new SourceTime(new RationalTime(5, 1))) == mapping.TimelineEnd,
            "源出点与项目终点不一致");
        Throws<ArgumentOutOfRangeException>(() => mapping.Map(new SourceTime(new RationalTime(6, 1))),
            "片段外时间应被拒绝");
    }

    /// <summary>一万二千个不等间隔 PTS 逐项映射须等于绝对时间，不能累加圆整帧数。</summary>
    private static void VariableFrameRateDoesNotAccumulateDrift(CancellationToken cancellationToken)
    {
        const long firstPts = -90_000;
        const int timeBase = 90_000;
        var steps = new[] { 3_000, 1_500, 4_500, 2_997, 3_003 };
        var mapping = new ClipTimeMapping(new SourceTime(new RationalTime(0, 1)),
            new SourceTime(new RationalTime(3_600, 1)), new ProjectTime(new RationalTime(10, 1)));
        var rate = new FrameRate(30, 1);
        var pts = firstPts;
        long roundedIntervals = 0;
        for (var index = 0; index < 12_000; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = steps[index % steps.Length];
            pts += step;
            var source = SourceTime.FromPts(pts, firstPts, 1, timeBase);
            var fromPts = mapping.MapPts(pts, firstPts, 1, timeBase);
            var absolute = mapping.Map(source);
            var expected = new RationalTime(10, 1).Add(new RationalTime(pts - firstPts, timeBase));
            Assert(fromPts == absolute && fromPts.Value == expected, $"VFR 第 {index} 帧映射漂移");
            Assert(mapping.SourceAt(fromPts) == source, $"VFR 第 {index} 帧逆映射漂移");
            roundedIntervals += rate.FrameAt(new ProjectTime(new RationalTime(step, timeBase)), TimeRounding.Nearest);
        }
        var finalFrame = rate.FrameAt(mapping.MapPts(pts, firstPts, 1, timeBase), TimeRounding.Nearest);
        Assert(finalFrame == 12_300, "绝对 PTS 的最终帧位置错误（含 10 秒项目起点）");
        Assert(roundedIntervals == 14_400, "测试用逐间隔舍入未暴露累计漂移");
        Assert(finalFrame - 300 != roundedIntervals, "VFR 不应按每段舍入结果累计帧号");
    }

    /// <summary>负 PTS 偏移、默认值、非法时间基与不可表示分母均应明确处理。</summary>
    private static void InvalidAndOverflowValues(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert(default(RationalTime) == new RationalTime(0, 1), "默认时间值应是零");
        Assert(SourceTime.FromPts(-45_000, -90_000, 1, 90_000).Value == new RationalTime(1, 2),
            "负起始 PTS 应按首帧归零");
        Throws<ArgumentOutOfRangeException>(() => SourceTime.FromPts(-90_001, -90_000, 1, 90_000),
            "首帧前的负坐标应被拒绝");
        Throws<ArgumentOutOfRangeException>(() => SourceTime.FromPts(1, 0, 0, 90_000),
            "零时间基应被拒绝");
        Throws<ArgumentOutOfRangeException>(() => new RationalTime(43_201, 1), "超过十二小时应被拒绝");
        Throws<OverflowException>(() => new RationalTime(1, long.MaxValue)
            .Add(new RationalTime(1, long.MaxValue - 1)), "无法精确表示的分母应被拒绝");
        Throws<ArgumentOutOfRangeException>(() => new FrameRate(0, 1), "零帧率应被拒绝");
        Throws<ArgumentOutOfRangeException>(() => new FrameRate(30, 1).FrameStart(-1),
            "负帧序号应被拒绝");
    }

    /// <summary>为独立测试入口提供带原因的断言。</summary>
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>只把指定异常类型视为边界拒绝通过。</summary>
    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException(message);
    }
}
