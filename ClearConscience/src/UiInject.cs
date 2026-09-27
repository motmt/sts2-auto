using System;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;

namespace ClearConscience;

/// <summary>
/// 在结算屏（GameOverScreen）上加一个『问心无愧』按钮，做法与塔一的 mod 一致：
/// 复制游戏原生的『返回主菜单』按钮（保留它的美术/字体/中文渲染与点击手感），
/// 改名、改文本，然后追加我们自己的点击处理（保留存档）并触发返回主菜单。
/// 全部操作包在 try/catch 里：即便游戏 UI 结构变化，也只是按钮不出现，绝不会影响游戏。
/// </summary>
public static class UiInject
{
    public const string ButtonName = "ClearConscienceButton";

    public static void TryInject(Node screen)
    {
        try
        {
            if (screen == null || !Guard.PendingDefeatDecision) return;
            if (screen.GetNodeOrNull<Node>(ButtonName) != null) return; // 已加过

            Node anchor = screen.GetNodeOrNull<Node>("%MainMenuButton");
            if (anchor == null)
            {
                Guard.Note("结算屏未找到 MainMenuButton，跳过按钮注入");
                return;
            }
            Node parent = anchor.GetParent();
            if (parent == null) return;

            // 复制节点：含子节点/脚本/实例数据，但不复制信号连接（14 = Groups|Scripts|Instancing）
            var dup = anchor.Duplicate(14);
            if (dup is not Node node)
            {
                Guard.Note("复制按钮失败，跳过注入");
                return;
            }
            node.Name = ButtonName;
            parent.AddChild(node);

            // 文本：优先用游戏的 MegaLabel.SetTextAutoSize（自适应），失败则退回设置 text 属性
            try
            {
                Node label = node.GetNodeOrNull<Node>("Label");
                if (label != null)
                {
                    var mi = label.GetType().GetMethod("SetTextAutoSize");
                    if (mi != null) mi.Invoke(label, new object[] { "问心无愧" });
                    else label.Set("text", "问心无愧");
                }
            }
            catch (Exception e)
            {
                ModEntry.Logger.Warn("设置按钮文本失败: " + e);
            }

            // 点击：保留存档 + 触发返回主菜单
            try
            {
                node.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(btn => OnPressed(screen, btn)));
            }
            catch (Exception e)
            {
                ModEntry.Logger.Warn("连接按钮信号失败: " + e);
            }

            // 位置：等结算屏按钮动画结束（约 0.5s）后，摆到『返回主菜单』附近
            TaskHelper.RunSafely(PlaceAsync(screen, node, anchor));
            Guard.Note("已在结算屏添加『问心无愧』按钮");
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("注入按钮失败: " + e);
        }
    }

    private static async Task PlaceAsync(Node screen, Node node, Node anchor)
    {
        try
        {
            // 结算屏按钮 _Ready 时是透明的（transparentBlack），要靠 OnEnable 动画才显示；
            // 复制品不在 game over screen 的按钮列表里，所以这里主动让它显示出来。
            await Task.Delay(120);
            if (!GodotObject.IsInstanceValid(node)) return;
            MakeVisible(node);

            // 参考按钮：优先用同屏可见的『继续』（它的位置才是玩家真正看到的坐标）
            Node reference = screen.GetNodeOrNull<Node>("%ContinueButton") ?? anchor;

            // 关键：游戏把未就位的按钮临时放在 y+190 的隐藏位，等它滑回正常位置再取坐标
            float refY = float.MaxValue;
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(100);
                if (!GodotObject.IsInstanceValid(node) || !GodotObject.IsInstanceValid(reference)) return;
                try
                {
                    refY = reference.Get("position").AsVector2().Y;
                }
                catch (Exception)
                {
                    // 忽略读取失败
                }
                if (refY < 1000f) break; // 已滑回屏幕内，视为就位
            }

            Vector2 refPos = reference.Get("position").AsVector2();

            // 按钮高度（取不到就用经验值 66）
            float bh = 66f;
            try
            {
                Variant sz = reference.Get("size");
                if (sz.VariantType == Variant.Type.Vector2)
                {
                    float h = sz.AsVector2().Y;
                    if (h > 10f && h < 300f) bh = h;
                }
            }
            catch (Exception)
            {
                // 忽略
            }

            float gap = bh + 90f; // 与『继续』拉开一点（之前 bh+24 视觉上仍贴在一起）
            Vector2 pos;
            if (refY >= 1000f)
            {
                // 兜底：参考按钮始终没就位（极端情况）→ 用屏幕比例放在中下方，避免出屏
                float vw = 1920f, vh = 1080f;
                try
                {
                    Viewport vp0 = node.GetViewport();
                    if (vp0 != null)
                    {
                        Vector2 vs = vp0.GetVisibleRect().Size;
                        if (vs.X > 100f) vw = vs.X;
                        if (vs.Y > 100f) vh = vs.Y;
                    }
                }
                catch (Exception)
                {
                    // 用默认 1920x1080
                }
                pos = new Vector2(vw * 0.5f - 130f, vh * 0.78f);
            }
            else
            {
                pos = new Vector2(refPos.X, refPos.Y - gap); // 紧贴在『继续』上方
                try
                {
                    Viewport vp = node.GetViewport();
                    float h = vp != null ? vp.GetVisibleRect().Size.Y : 0f;
                    if (h > 0f && pos.Y < 60f) pos = new Vector2(refPos.X, refPos.Y + gap); // 上方放不下 → 放下方
                }
                catch (Exception)
                {
                    // 忽略 viewport 取不到
                }
            }

            node.Set("position", pos);
            try
            {
                node.Set("scale", reference.Get("scale"));
            }
            catch (Exception)
            {
                // scale 取不到就忽略
            }
            ModEntry.Logger.Info("[问心无愧] 参考按钮就位 y=" + refY + " 参考坐标=" + refPos + " -> 新按钮 " + pos + " gap=" + gap);

            // 再确认一次，防止别处的动画/布局把位置挪走（这就是上次和『继续』重叠的原因）
            await Task.Delay(300);
            if (!GodotObject.IsInstanceValid(node)) return;
            KillTweens(node);
            node.Set("position", pos);
            try
            {
                ModEntry.Logger.Info("[问心无愧] 位置确认: " + node.Get("position").AsVector2());
            }
            catch (Exception)
            {
                // 忽略日志失败
            }
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("摆放按钮失败: " + e);
        }
    }

    /// <summary>
    /// 让复制出来的按钮真正显示。
    /// 注意：这里**不能**调用 OnEnable() —— 它的滑入动画会在我们摆好位置之后
    /// 又把按钮 tween 回复制时记录的原始位置，结果就是和『继续』重叠。
    /// 直接设 visible + modulate 即可（样式来自复制来的子节点）。
    /// </summary>
    private static void MakeVisible(Node node)
    {
        try
        {
            KillTweens(node);
            node.Set("visible", true);
            node.Set("modulate", Colors.White);
            ModEntry.Logger.Info("[问心无愧] 按钮可视: visible=" + node.Get("visible") + " modulate=" + node.Get("modulate"));
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("设置可见性失败: " + e);
        }
    }

    /// <summary>把节点上可能存在的 Tween 全部杀掉（防止动画把我们的坐标改回去）。</summary>
    private static void KillTweens(Node node)
    {
        try
        {
            foreach (string field in new[] { "_tween", "_hoverTween" })
            {
                var fi = AccessTools.Field(node.GetType(), field);
                if (fi?.GetValue(node) is Tween tween && GodotObject.IsInstanceValid(tween))
                {
                    tween.Kill();
                }
            }
        }
        catch (Exception)
        {
            // 没有 tween 就算了
        }
    }

    private static void OnPressed(Node screen, NButton btn)
    {
        bool isHost = false;
        try
        {
            RunManager rm = RunManager.Instance;
            isHost = rm?.NetService != null && rm.NetService.Type == NetGameType.Host;
        }
        catch (Exception)
        {
            // 取不到就按单机处理
        }
        try
        {
            Guard.KeepSaveRequested = true;
            Guard.PendingDefeatDecision = false;
            Guard.AutoContinue = !isHost; // 单机：自动继续直达本层；联机：由房主在多人菜单『读取』
            Guard.Note(isHost
                ? "点击『问心无愧』：保留存档（联机请回多人菜单点『读取』续开）"
                : "点击『问心无愧』：保留存档并自动继续到本层");
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("按钮点击处理失败: " + e);
        }
        // 复用游戏原逻辑返回主菜单（我们的复制品没有原信号，需手动调用）
        try
        {
            var mi = AccessTools.Method(typeof(NGameOverScreen), "OnMainMenuButtonPressed");
            if (mi != null)
            {
                mi.Invoke(screen, new object[] { btn });
            }
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("调用返回主菜单失败: " + e);
        }
    }

    /// <summary>主菜单刷新后（此时『继续』已可用）：自动点一次继续，直达本层。</summary>
    public static void StartAutoContinue(NMainMenu menu)
    {
        TaskHelper.RunSafely(AutoContinueAsync(menu));
    }

    private static async Task AutoContinueAsync(NMainMenu menu)
    {
        try
        {
            await Task.Delay(900); // 等主菜单显示/动画稳定
            if (!GodotObject.IsInstanceValid(menu))
            {
                Guard.Note("主菜单已失效，请手动点『继续』");
                return;
            }
            var mi = AccessTools.Method(typeof(NMainMenu), "OnContinueButtonPressed");
            if (mi == null)
            {
                Guard.Note("未找到继续逻辑，请手动点『继续』");
                return;
            }
            mi.Invoke(menu, new object[] { null });
            Guard.Note("已自动继续：正在回到本层战斗");
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("自动继续失败: " + e);
            Guard.Note("自动继续失败，请手动点『继续』");
        }
    }
}
