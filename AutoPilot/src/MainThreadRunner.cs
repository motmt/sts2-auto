using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace AutoPilot;

/// <summary>
/// 主线程帧驱动调度器（黑屏/主循环静默死亡根治）。
///
/// ── 为什么需要它 ─────────────────────────────────────────────────────
/// Godot 的 C# 运行时**没有安装自定义 SynchronizationContext**（已核实反编译源码中
/// 无任何 SynchronizationContext 使用）。因此 <c>await Task.Delay(...)</c> 的续体会
/// 默认落到 .NET **线程池**线程上执行，而不是 Godot 主线程。
///
/// 后果（实测）：
///   1. 线程池续体里访问 Godot 节点 → 引擎报
///      "Changing the name to nodes inside the SceneTree is only allowed from the main thread"
///      "Can't add child ... already has a parent"（日志里成片的这类 ERROR）。
///   2. 战斗胜利 / 房间切换时，场景树正在拆卸重建，主线程被长时间占用；
///      此时线程池续体对"已释放节点"的属性读取可能抛异常或被静默吞掉 →
///      AutoPilot 主循环在某次 await 之后**再也不被唤醒**（日志停在"出牌"，
///      既没有"战斗结束"也没有"主循环已退出"）→ 画面停在切换中途 = 黑屏。
///
/// ── 做法 ─────────────────────────────────────────────────────────────
/// 用 Godot 的 <c>SceneTree.ProcessFrame</c>（每帧、必定运行在主线程）作为心跳，
/// 在主线程上驱动一个"等待队列"：任何 <see cref="NextFrameAsync"/> 都在下一帧由
/// 主线程唤醒续体。这样 AutoPilot 的每一步都在主线程执行，既不触发线程安全错误，
/// 也不会因线程池续体丢失而静默死亡。
/// </summary>
public static class MainThreadRunner
{
    private static bool _installed;
    private static SceneTree _tree;

    // 等待下一帧的续体队列（由主线程在 ProcessFrame 里全部唤醒）
    private static readonly ConcurrentQueue<TaskCompletionSource<bool>> _waiters = new();

    // 诊断：已运行的帧数（供看门狗判断"主线程还活着"）
    public static long Frames;

    /// <summary>安装帧驱动（幂等，必须在主线程调用）。</summary>
    public static void Install()
    {
        if (_installed) return;
        try
        {
            _tree = Engine.GetMainLoop() as SceneTree;
            if (_tree == null)
            {
                ModEntry.Logger.Warn("[AutoPilot] MainThreadRunner: 拿不到 SceneTree，退回原调度");
                return;
            }
            _tree.ProcessFrame += OnProcessFrame;
            _installed = true;
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("[AutoPilot] MainThreadRunner 安装失败: " + e.Message);
        }
    }

    /// <summary>是否已成功安装帧驱动。</summary>
    public static bool Installed => _installed;

    private static void OnProcessFrame()
    {
        Frames++;
        // 每帧唤醒所有等待者（它们在主线程上继续执行）
        while (_waiters.TryDequeue(out TaskCompletionSource<bool> tcs))
        {
            try { tcs.TrySetResult(true); }
            catch { }
        }
    }

    /// <summary>
    /// 等待到下一帧（续体保证在 Godot 主线程执行）。
    /// 替代 <c>Task.Delay</c> 用于主循环 / 界面处理的等待。
    /// </summary>
    public static Task NextFrameAsync(CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters.Enqueue(tcs);
        if (ct.CanBeCanceled)
        {
            ct.Register(() => { try { tcs.TrySetCanceled(ct); } catch { } });
        }
        return tcs.Task;
    }

    /// <summary>
    /// 等待约 N 毫秒（按帧折算，120 FPS ≈ 8.3ms/帧）。
    /// 相比 Task.Delay，它**保证**续体在主线程，且不会因线程池调度丢失。
    /// </summary>
    public static async Task DelayAsync(int ms, CancellationToken ct = default)
    {
        if (ms <= 0) { await NextFrameAsync(ct); return; }
        // 至少 1 帧；按 60FPS 保守折算，避免过度拉长
        int frames = Math.Max(1, (int)Math.Ceiling(ms / 16.6));
        for (int i = 0; i < frames; i++)
        {
            if (ct.IsCancellationRequested) return;
            await NextFrameAsync(ct);
        }
    }

    /// <summary>主线程正在运行（用于看门狗判断引擎是否卡死）。</summary>
    public static bool MainThreadAlive(long lastFrameSeen, int windowFrames = 120)
        => Frames - lastFrameSeen < windowFrames;
}
