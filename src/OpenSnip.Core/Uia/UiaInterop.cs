using System.Runtime.InteropServices;

namespace OpenSnip.Core;

/// <summary>
/// UIAutomation 原生 COM interop(手写 ComImport,不引 WPF / WindowsDesktop Framework)。
/// <para>
/// vtable 槽位顺序的权威依据:Windows SDK 10.0.26100 UIAutomationClient.h
/// (IUIAutomation 第 18849 行起 / IUIAutomationElement 第 1592 行起 / IUIAutomationTreeWalker 第 3450 行起),
/// 逐槽核对;只声明到引擎实际使用的最后一个槽位,未使用的槽位一律占位占满,绝不能少。
/// </para>
/// </summary>
internal static unsafe class UiaInterop
{
    public const uint CoinitMultithreaded = 0;   // COINIT_MULTITHREADED
    private const uint ClsCtxInprocServer = 1;

    private static readonly Guid ClsidCUiAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");
    private static readonly Guid IidIUIAutomation = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    /// <summary>当前线程 CoInitializeEx(MTA)。</summary>
    public static int InitializeCom() => CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);

    /// <summary>创建 UIA 自动化对象(in-proc);失败返回 null(吞异常,契约红线)。</summary>
    public static IUIAutomation? CreateAutomation()
    {
        try
        {
            var clsid = ClsidCUiAutomation;
            var iid = IidIUIAutomation;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxInprocServer, ref iid, out IntPtr ptr);
            if (hr != 0 || ptr == IntPtr.Zero)
            {
                PerfLog.Log("Core", $"CoCreateInstance(CUIAutomation) 失败 hr=0x{hr:X8}");
                return null;
            }
            // GetObjectForIUnknown 内部 AddRef;随后释放我们持有的原始引用
            var rcw = Marshal.GetObjectForIUnknown(ptr);
            _ = Marshal.Release(ptr);
            var result = (IUIAutomation)rcw;
            GC.KeepAlive(rcw);
            return result;
        }
        catch (Exception ex)
        {
            PerfLog.Log("Core", $"UIA 创建异常(已吞掉):{ex.Message}");
            return null;
        }
    }
}

/// <summary>物理像素点(UIA ElementFromPoint 用)。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct UiaPoint
{
    public int X;
    public int Y;

    public UiaPoint(int x, int y)
    {
        X = x;
        Y = y;
    }
}

/// <summary>物理像素矩形(UIA BoundingRectangle 原生 RECT,4×int)。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct UiaRect
{
    public int Left;
    public int Top;
    public int Width;
    public int Height;
}

/// <summary>
/// IUIAutomation:声明至第 12 槽 get_ControlViewWalker。
/// </summary>
[ComImport]
[Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation
{
    // 1-2:vtable 占位(不调用)
    [PreserveSig]
    int CompareElements(IntPtr e1, IntPtr e2);

    [PreserveSig]
    int CompareRuntimeIds(IntPtr id1, IntPtr id2);

    // 3:vtable 占位(不调用)
    [return: MarshalAs(UnmanagedType.Interface)]
    object GetRootElement();

    // 4
    IUIAutomationElement ElementFromHandle(IntPtr hwnd);

    // 5
    IUIAutomationElement ElementFromPoint(UiaPoint pt);

    // 6-10:vtable 占位(不调用)
    [return: MarshalAs(UnmanagedType.Interface)]
    object GetFocusedElement();

    [return: MarshalAs(UnmanagedType.Interface)]
    object GetRootElementBuildCache(IntPtr cacheRequest);

    [return: MarshalAs(UnmanagedType.Interface)]
    object ElementFromHandleBuildCache(IntPtr hwnd, IntPtr cacheRequest);

    [return: MarshalAs(UnmanagedType.Interface)]
    object ElementFromPointBuildCache(UiaPoint pt, IntPtr cacheRequest);

    [return: MarshalAs(UnmanagedType.Interface)]
    object GetFocusedElementBuildCache(IntPtr cacheRequest);

    // 11:vtable 占位(不调用)
    [return: MarshalAs(UnmanagedType.Interface)]
    object CreateTreeWalker(IntPtr condition);

    // 12
    IUIAutomationTreeWalker GetControlViewWalker();
}

/// <summary>
/// IUIAutomationElement:声明至第 41 槽 get_CurrentBoundingRectangle。
/// 未使用的属性槽以占位成员占满(只保证槽位序,不调用)。
/// BSTR 属性的封送特性标注在 getter 访问器上([property:] 位置无效)。
/// </summary>
[ComImport]
[Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    // 1-17:方法占位(不调用)
    [PreserveSig]
    int SetFocus();

    [PreserveSig]
    int GetRuntimeId(out IntPtr runtimeId);

    [PreserveSig]
    IntPtr FindFirst(int scope, IntPtr condition);

    [PreserveSig]
    IntPtr FindAll(int scope, IntPtr condition);

    [PreserveSig]
    IntPtr FindFirstBuildCache(int scope, IntPtr condition, IntPtr cacheRequest);

    [PreserveSig]
    IntPtr FindAllBuildCache(int scope, IntPtr condition, IntPtr cacheRequest);

    [PreserveSig]
    IntPtr BuildUpdatedCache(IntPtr cacheRequest);

    [PreserveSig]
    int GetCurrentPropertyValue(int propertyId, out IntPtr value);

    [PreserveSig]
    int GetCurrentPropertyValueEx(int propertyId, int ignoreDefaultValue, out IntPtr value);

    [PreserveSig]
    int GetCachedPropertyValue(int propertyId, out IntPtr value);

    [PreserveSig]
    int GetCachedPropertyValueEx(int propertyId, int ignoreDefaultValue, out IntPtr value);

    [PreserveSig]
    int GetCurrentPatternAs(int patternId, ref Guid riid, out IntPtr pattern);

    [PreserveSig]
    int GetCachedPatternAs(int patternId, ref Guid riid, out IntPtr pattern);

    [PreserveSig]
    int GetCurrentPattern(int patternId, out IntPtr pattern);

    [PreserveSig]
    int GetCachedPattern(int patternId, out IntPtr pattern);

    [PreserveSig]
    IntPtr GetCachedParent();

    [PreserveSig]
    IntPtr GetCachedChildren();

    // 18 CurrentProcessId
    int CurrentProcessId { get; }

    // 19 CurrentControlType(UIA_CONTROLTYPE_ID)
    int CurrentControlType { get; }

    // 20 占位
    string CurrentLocalizedControlType
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        get;
    }

    // 21 CurrentName
    string CurrentName
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        get;
    }

    // 22-23 占位
    string CurrentAcceleratorKey
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        get;
    }

    string CurrentAccessKey
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        get;
    }

    // 24-25 占位
    int CurrentHasKeyboardFocus { get; }

    int CurrentIsKeyboardFocusable { get; }

    // 26 CurrentIsEnabled
    int CurrentIsEnabled { get; }

    // 27 占位
    string CurrentAutomationId
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        get;
    }

    // 28 CurrentClassName
    string CurrentClassName
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        get;
    }

    // 29 占位
    string CurrentHelpText
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        get;
    }

    // 30 占位
    int CurrentCulture { get; }

    // 31-33 占位
    int CurrentIsControlElement { get; }

    int CurrentIsContentElement { get; }

    int CurrentIsPassword { get; }

    // 34 CurrentNativeWindowHandle
    IntPtr CurrentNativeWindowHandle { get; }

    // 35 占位
    string CurrentItemType
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        get;
    }

    // 36 CurrentIsOffscreen
    int CurrentIsOffscreen { get; }

    // 37 占位
    int CurrentOrientation { get; }

    // 38 占位
    string CurrentFrameworkId
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        get;
    }

    // 39 占位
    int CurrentIsRequiredForForm { get; }

    // 40 占位
    string CurrentItemStatus
    {
        [return: MarshalAs(UnmanagedType.BStr)]
        get;
    }

    // 41 CurrentBoundingRectangle(物理像素 RECT)
    UiaRect CurrentBoundingRectangle { get; }
}

/// <summary>
/// IUIAutomationTreeWalker:声明至第 6 槽 NormalizeElement。
/// </summary>
[ComImport]
[Guid("4042c624-389c-4afc-a630-9df854a541fc")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTreeWalker
{
    // 1
    IUIAutomationElement GetParentElement(IUIAutomationElement element);

    // 2
    IUIAutomationElement GetFirstChildElement(IUIAutomationElement element);

    // 3 占位
    IUIAutomationElement GetLastChildElement(IUIAutomationElement element);

    // 4
    IUIAutomationElement GetNextSiblingElement(IUIAutomationElement element);

    // 5-6 占位
    IUIAutomationElement GetPreviousSiblingElement(IUIAutomationElement element);

    IUIAutomationElement NormalizeElement(IUIAutomationElement element);
}
