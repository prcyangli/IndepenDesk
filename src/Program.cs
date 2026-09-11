namespace IndepenDesk;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(true, "IndepenDesk_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show(L.T("msg.alreadyRunning"), "IndepenDesk",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(new TrayApp());
    }
}
