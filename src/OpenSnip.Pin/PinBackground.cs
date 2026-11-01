namespace OpenSnip.Pin;

/// <summary>贴图背景模式(契约 §11)。</summary>
public enum PinBackground
{
    /// <summary>
    /// 透明(默认):无背景层,ULW 逐像素 alpha —— 内容透明像素区域实时透出背后内容且鼠标可点穿(M1 行为)。
    /// </summary>
    Transparent,

    /// <summary>
    /// 伪透明:截取贴图当前占据区域背后的屏幕快照垫底,内容叠加其上(标准 over 合成)。
    /// 创建后位置/尺寸变化时自动重截(150ms 防抖,重截瞬间窗口隐去一帧避免把贴图自己截进背景)。
    /// 回退(浅棋盘 + PerfLog):窗口跨屏或出屏、截屏异常、捕获到纯黑画面(隐藏桌面空桌特征,SPEC §8.4)。
    /// </summary>
    PseudoTransparent,

    /// <summary>深色棋盘:内容透明区以深色棋盘格垫底(可视化 alpha 分布;整窗不透明可命中)。</summary>
    DarkCheckerboard,

    /// <summary>浅色棋盘:内容透明区以浅色棋盘格垫底;同时是伪透明的回退背景(契约 §11)。</summary>
    LightCheckerboard,
}
