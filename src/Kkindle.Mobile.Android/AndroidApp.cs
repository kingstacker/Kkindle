using Android.App;
using Android.Runtime;
using Avalonia.Android;
using Kkindle.Mobile;

namespace Kkindle.AndroidHead;

/// <summary>
/// Process entry point. Avalonia's Android application creates the lifetime
/// before the first activity, so platform adapters must be published here.
/// </summary>
[Application]
public class AndroidApp : AvaloniaAndroidApplication<MobileApp>
{
    protected AndroidApp(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        MobileApp.Services = AndroidMobileHost.Create(this);
        base.OnCreate();
    }
}
