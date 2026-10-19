// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
// 抽取自 WinUI Gallery Helpers/TitleBarHelper.cs，固定版本见 LICENSE.WinUI-Gallery.txt。
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace ModelDesk;

internal static class GalleryTitleBarHelper
{
    public static void ApplySystemThemeToCaptionButtons(Window window, ElementTheme currentTheme)
    {
        var foregroundColor = currentTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
        window.AppWindow.TitleBar.ButtonForegroundColor = foregroundColor;
        window.AppWindow.TitleBar.ButtonHoverForegroundColor = foregroundColor;
        var backgroundHoverColor = currentTheme == ElementTheme.Dark
            ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0);
        window.AppWindow.TitleBar.ButtonHoverBackgroundColor = backgroundHoverColor;
    }
}
