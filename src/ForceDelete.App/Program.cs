using ForceDelete.Core;

namespace ForceDelete.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Enable the privileges the ownership step depends on, once at startup.
        var missingPrivileges = PrivilegeManager.EnableDeletePrivileges();

        Application.Run(new MainForm(missingPrivileges));
    }
}
