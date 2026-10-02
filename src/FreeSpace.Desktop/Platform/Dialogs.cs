using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using FreeSpace.Contracts.Storage;

namespace FreeSpace.Desktop.Platform;

/// <summary>Small modal dialogs, built in code: they are simple forms that do not need their own XAML.</summary>
internal static class Dialogs
{
    public static async Task<string?> PromptAsync(Window owner, string title, string message, string initialValue)
    {
        var input = new TextBox { Text = initialValue, MinWidth = 320 };
        var result = await ShowAsync<string?>(owner, title, new StackPanel
        {
            Spacing = 8,
            Children = { new TextBlock { Text = message }, input },
        }, ok: () => input.Text, focus: input);
        return result;
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message) =>
        await ShowAsync<bool>(owner, title, new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 420 },
            ok: () => true, okText: "Continue");

    public static Task MessageAsync(Window owner, string title, string message) =>
        ShowAsync<bool>(owner, title, new SelectableTextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 460 },
            ok: () => true, okText: "OK", showCancel: false);

    public static async Task<ConnectS3Request?> S3ConnectionAsync(Window owner)
    {
        TextBox Field(string watermark, string text = "") => new() { PlaceholderText = watermark, Text = text, MinWidth = 360 };
        var name = Field("Display name", "My bucket");
        var endpoint = Field("Endpoint (empty for AWS) — e.g. https://<account>.r2.cloudflarestorage.com");
        var region = Field("Region", "auto");
        var bucket = Field("Bucket");
        var prefix = Field("Prefix (optional)", "freespace");
        var keyId = Field("Access key id");
        var secret = new TextBox { PlaceholderText = "Secret access key", PasswordChar = '•', MinWidth = 360 };
        var quotaGb = Field("Quota in GB (optional)");
        var error = new TextBlock { Classes = { "error" } };

        var form = new StackPanel { Spacing = 8, Children = { name, endpoint, region, bucket, prefix, keyId, secret, quotaGb, error } };
        return await ShowAsync<ConnectS3Request?>(owner, "Connect S3-compatible storage", form, ok: () =>
        {
            long? quota = null;
            if (!string.IsNullOrWhiteSpace(quotaGb.Text))
            {
                if (!double.TryParse(quotaGb.Text, out var gb) || gb <= 0)
                {
                    error.Text = "Quota must be a positive number of GB.";
                    return null;
                }
                quota = (long)(gb * 1024 * 1024 * 1024);
            }
            if (string.IsNullOrWhiteSpace(bucket.Text) || string.IsNullOrWhiteSpace(keyId.Text) || string.IsNullOrWhiteSpace(secret.Text))
            {
                error.Text = "Bucket, access key id and secret are required.";
                return null;
            }
            return new ConnectS3Request(name.Text ?? "S3", string.IsNullOrWhiteSpace(endpoint.Text) ? null : endpoint.Text.Trim(),
                string.IsNullOrWhiteSpace(region.Text) ? "us-east-1" : region.Text.Trim(), bucket.Text.Trim(), prefix.Text?.Trim(),
                keyId.Text.Trim(), secret.Text, null, quota);
        }, okText: "Connect", focus: bucket, keepOpenOnNull: true);
    }

    /// <param name="ok">Produces the result when OK is pressed.</param>
    /// <param name="keepOpenOnNull">Treat a null result from <paramref name="ok"/> as "invalid input, stay open".</param>
    private static async Task<T> ShowAsync<T>(Window owner, string title, Control body, Func<T> ok, string okText = "OK",
        bool showCancel = true, Control? focus = null, bool keepOpenOnNull = false)
    {
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Classes = { "glassDialog" },
        };

        T result = default!;
        var okButton = new Button { Content = okText, IsDefault = true, MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center, Classes = { "gel" }, Padding = new Thickness(20, 9) };
        okButton.Click += (_, _) =>
        {
            var value = ok();
            if (keepOpenOnNull && value is null) return;
            result = value;
            window.Close();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { okButton } };
        if (showCancel)
        {
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            cancel.Click += (_, _) => window.Close();
            buttons.Children.Add(cancel);
        }

        window.Content = new StackPanel { Margin = new Thickness(26), Spacing = 18, Children = { body, buttons } };
        if (focus is not null) window.Opened += (_, _) => focus.Focus(NavigationMethod.Tab);
        await window.ShowDialog(owner);
        return result;
    }
}
