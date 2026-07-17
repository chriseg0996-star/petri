using UnityEditor;

namespace Petri.EditorTools
{
    /// <summary>Launch straight into Play mode from the command line:
    /// Unity.exe -projectPath &lt;project&gt; -executeMethod Petri.EditorTools.PlayFromCli.EnterPlay
    /// The main menu builds itself from code on play, so no scene set-up is needed.</summary>
    public static class PlayFromCli
    {
        public static void EnterPlay() => EditorApplication.EnterPlaymode();
    }
}
