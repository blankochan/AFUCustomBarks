using HarmonyLib;
using Il2Cpp;
using Il2CppCustomCharacters;
using Il2CppPhoton.Client;
using Il2CppPhoton.Realtime;
using Il2CppUI_Localization;
using MelonLoader.Utils;
using MelonLoader;
using System.Collections;
using UnityEngine;

[assembly: MelonInfo(typeof(CustomBarks.Core), "CustomBarks", "1.0.0", "Azore", null)]
[assembly: MelonGame("Videocult", "Airframe")]

namespace CustomBarks
{
    public class Core : MelonMod
    {
        internal static MelonLogger.Instance Logger => Melon<CustomBarks.Core>.Logger;

        public static Dictionary<string, string> LocalCustomBarks = new();
        private static string customBarksFolderPath;
        private static int originalBarksCount = -1;

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("Custom Barks Mod active...");

            customBarksFolderPath = Path.Combine(MelonEnvironment.UserDataDirectory, "Custom Barks");
            if (!Directory.Exists(customBarksFolderPath))
            {
                Directory.CreateDirectory(customBarksFolderPath);
                string examplePath = Path.Combine(customBarksFolderPath, "example_pack.txt");

                const string example_pack_content = @"
//This is an example of a Bark File. They are written in bark_key=BarkText. New line = new bark.
backrooms_1=And the best part is...
backrooms_2=...you can eat them!
                ";

                File.WriteAllText(examplePath, example_pack_content);
            }

            PreloadLocalBarks();
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            MelonCoroutines.Start(DelayedSyncPipeline());
        }

        private static IEnumerator DelayedSyncPipeline()
        {
            int gaurd = 1000;
            while (gaurd >= 0)
            {
                if (PhotonController.instance.client.IsConnectedAndReady)
                {
                    SyncBarksWithRoomRegistry();
                }
                else yield return new WaitForSeconds(1f);
            }
        }

        private static void PreloadLocalBarks()
        {
            LocalCustomBarks.Clear();
            try
            {
                string[] packFiles = Directory.GetFiles(customBarksFolderPath, "*.txt");
                foreach (string file in packFiles)
                {
                    string[] lines = File.ReadAllLines(file);
                    foreach (string line in lines)
                    {
                        if (string.IsNullOrEmpty(line.Trim()) || line.StartsWith("//"))
                            continue;

                        int separatorIndex = line.IndexOf('=');
                        if (separatorIndex > 0)
                        {
                            string key = line.Substring(0, separatorIndex).Trim();
                            string text = line.Substring(separatorIndex + 1).Trim();

                            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(text))
                            {
                                LocalCustomBarks.Add(key, text);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error($"Error loading external barks: {e.Message}");
                Logger.BigError(System.Environment.StackTrace);
            }
        }

        public static void SyncBarksWithRoomRegistry()
        {
            try
            {
                PhotonController controllerInstance = PhotonController.instance;

                if (controllerInstance.client.CurrentRoom == null)
                {
                    InjectBarksFromList(LocalCustomBarks);
                    return;
                }

                var client = controllerInstance.client;
                var room = client.CurrentRoom;

                var myProps = client.LocalPlayer.CustomProperties;
                if (!myProps.ContainsKey("AzoreCustomBarks"))
                {
                    PhotonHashtable myBarksTable = new PhotonHashtable();
                    int barkIndex = 0;
                    foreach (var bark in LocalCustomBarks)
                    {
                        string packedData = $"{bark.Key}:{bark.Value}";
                        myBarksTable.Add(barkIndex.ToString(), packedData);
                        barkIndex++;
                    }

                    PhotonHashtable playerPropsWrapper = new();
                    playerPropsWrapper.Add("AzoreCustomBarks", myBarksTable);

                    client.LocalPlayer.SetCustomProperties(playerPropsWrapper);
                    Logger.Msg("Barks saved to properties");
                }

                SortedDictionary<int, Dictionary<string, string>> globalNetworkRegistry = new();

                if (room.Players != null)
                {
                    foreach (var playerKvp in room.Players)
                    {
                        Player player = playerKvp.Value;
                        if (player == null || player.CustomProperties == null) continue;

                        int actorId = player.ActorNumber;
                        var playerProps = player.CustomProperties;

                        if (playerProps.ContainsKey("AzoreCustomBarks"))
                        {
                            var playerTable = playerProps["AzoreCustomBarks"].Cast<PhotonHashtable>();
                            if (playerTable == null || playerTable.Keys == null) continue;

                            Dictionary<string, string> playerBarksList = new();

                            int barkIndex = 0;
                            while (playerTable.ContainsKey(barkIndex.ToString()))
                            {
                                var valObj = playerTable[barkIndex.ToString()];
                                if (valObj != null)
                                {
                                    string rawData = valObj.ToString();
                                    int separator = rawData.IndexOf(':');
                                    if (separator > 0)
                                    {
                                        string netKey = rawData.Substring(0, separator);
                                        string netText = rawData.Substring(separator + 1);
                                        playerBarksList.Add(netKey, netText);
                                    }
                                }
                                barkIndex++;
                            }

                            globalNetworkRegistry[actorId] = playerBarksList;
                        }
                    }
                }

                Dictionary<string, string> finalFlatBarksToInject = new();

                foreach (var playerPacket in globalNetworkRegistry)
                {
                    foreach (var bark in playerPacket.Value)
                    {
                        if (!finalFlatBarksToInject.Any(b => b.Key == bark.Key)) // i dont know what this is doing -lizabeth
                        {
                            finalFlatBarksToInject.Add(bark.Key, bark.Value);
                        }
                    }
                }

                InjectBarksFromList(finalFlatBarksToInject);
            }
            catch (Exception e)
            {
                Logger.Error($"Error during player sync: {e.Message}");
                Logger.BigError(System.Environment.StackTrace);
            }
        }
        public static void InjectBarksFromList(Dictionary<string, string> barksToInject)
        {
            if (barksToInject.Count == 0) return;

            try
            {
                var characterLibrary = Resources.FindObjectsOfTypeAll<CustomCharactersLibrary>().FirstOrDefault();
                if (characterLibrary == null)
                {
                    Logger.Warning("Could not find CustomCharactersLibrary");
                    return;
                }
                BarksLibrary barkLibrary = characterLibrary.barksLibrary;

                if (originalBarksCount == -1)
                {
                    originalBarksCount = barkLibrary.barks.Count;
                }

                for (int i = barkLibrary.barks.Count - 1; i >= originalBarksCount; i--) // i would fix this but i dont know what this is even trying todo -lizabeth
                {
                    if (barkLibrary.barks[i] != null && barkLibrary.barks[i].name != null && barkLibrary.barks[i].name.StartsWith("NetBark_"))
                    {
                        barkLibrary.barks.RemoveAt(i);
                    }
                }

                int nextIndex = barkLibrary.barks.Count;

                foreach (var barkPair in barksToInject)
                {
                    BarksLibrary.Bark customBark = new BarksLibrary.Bark();
                    customBark.name = $"NetBark_{nextIndex}";
                    customBark.index = nextIndex;
                    customBark.barkText = barkPair.Value;
                    customBark.key = barkPair.Key;
                    customBark.hidden = false;

                    barkLibrary.barks.Add(customBark);
                    nextIndex++;
                }

                for (int i = 0; i < barkLibrary.barks.Count; i++)
                {
                    if (barkLibrary.barks[i] != null)
                    {
                        barkLibrary.barks[i].index = i;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error($"evil ass error: {e.Message}");
                Logger.BigError(System.Environment.StackTrace);
            }
        }

        [HarmonyPatch(typeof(PhotonController), nameof(PhotonController.OnPlayerPropertiesUpdate))]
        public static class QuantumPlayerPropertiesPatch
        {
            public static void Postfix()
            {
                CustomBarks.Core.SyncBarksWithRoomRegistry();
            }
        }

    }
}
