using Microsoft.AspNetCore.Components.WebView.Maui;

namespace Jotdash;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    public BlazorWebView WebView => blazorWebView;
}
