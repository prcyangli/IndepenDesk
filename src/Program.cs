namespace IndepenDesk;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "IndepenDesk_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("IndepenDesk zaten çalışıyor.", "IndepenDesk",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
