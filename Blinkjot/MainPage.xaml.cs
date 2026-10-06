using Microsoft.AspNetCore.Components.WebView.Maui;

namespace Blinkjot;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    public BlazorWebView WebView => blazorWebView;
}
