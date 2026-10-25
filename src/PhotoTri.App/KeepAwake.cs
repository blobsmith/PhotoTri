using System.Runtime.InteropServices;

namespace PhotoTri.App;

/// <summary>
/// Empêche la mise en veille automatique du PC tant que l'objet n'est pas libéré.
/// L'écran peut toujours s'éteindre. À appeler depuis le thread de l'interface, qui reste actif.
/// </summary>
sealed class KeepAwake : IDisposable
{
    const uint ES_CONTINUOUS = 0x80000000;
    const uint ES_SYSTEM_REQUIRED = 0x00000001;

    public KeepAwake() => SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);

    public void Dispose() => SetThreadExecutionState(ES_CONTINUOUS);

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint esFlags);
}
