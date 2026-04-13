using HcfScreensaver.Forms;
using HcfScreensaver.Models;

namespace HcfScreensaver;

public sealed class ScreensaverApplicationContext : ApplicationContext
{
    private int _openFormCount;

    public ScreensaverApplicationContext(ScreensaverSettings settings, bool previewMode, IntPtr previewHandle)
    {
        if (previewMode)
        {
            var previewForm = new ScreensaverForm(
                Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 800, 600),
                settings, true, previewHandle);
            RegisterForm(previewForm);
            previewForm.Show();
            return;
        }

        foreach (var screen in Screen.AllScreens)
        {
            var form = new ScreensaverForm(screen.Bounds, settings, false, IntPtr.Zero);
            RegisterForm(form);
            form.Show();
        }

        if (_openFormCount == 0)
            ExitThread();
    }

    private void RegisterForm(Form form)
    {
        _openFormCount++;
        form.FormClosed += (_, _) =>
        {
            _openFormCount--;
            if (_openFormCount <= 0)
                ExitThread();
        };
    }
}
