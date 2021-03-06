using System;
using System.Collections.Generic;
using System.Net;
using System.IO;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.GamerServices;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Media;
using Microsoft.Xna.Framework.Net;
using Microsoft.Xna.Framework.Storage;
using Lidgren.Network;
using InfiniminerShared;
using System.Text;
using Plexiglass.Client;

namespace Infiniminer
{
    public class InfiniminerGame : StateMasher.StateMachine
    {
        double timeSinceLastUpdate = 0;

        NetIncomingMessage msgBuffer;
        Song songTitle = null;

        public bool customColours = false;
        public Color red=Defines.IM_RED;
        public string redName = "Red";
        public Color blue = Defines.IM_BLUE;
        public string blueName = "Blue";

        public KeyBindHandler keyBinds = new KeyBindHandler();

        public bool anyPacketsReceived = false;

        public InfiniminerGame(string[] args)
        {
        }

        public void setServername(string newName)
        {
            propertyBag.serverName = newName;
        }

        public void JoinGame(IPEndPoint serverEndPoint)
        {
            anyPacketsReceived = false;
            // Clear out the map load progress indicator.
            propertyBag.MapLoadProgress = new bool[64,64];
            for (int i = 0; i < 64; i++)
                for (int j=0; j<64; j++)
                    propertyBag.MapLoadProgress[i,j] = false;

            // Create our connect message.
            NetOutgoingMessage connectBuffer = propertyBag.netClient.CreateMessage();
            connectBuffer.Write(propertyBag.PlayerContainer.PlayerHandle);
            connectBuffer.Write(Defines.INFINIMINER_VERSION);

            //Compression - will be ignored by regular servers
            connectBuffer.Write(true);

            // Connect to the server.
            propertyBag.netClient.Connect(serverEndPoint, connectBuffer);
        }

        public List<ServerInformation> EnumerateServers(float discoveryTime)
        {
            List<ServerInformation> serverList = new List<ServerInformation>();

            // Discover local servers.
            propertyBag.netClient.DiscoverLocalPeers(5565);
            NetIncomingMessage msgBuffer;
            float timeTaken = 0;
            while (timeTaken < discoveryTime)
            {
                while ((msgBuffer = propertyBag.netClient.ReadMessage()) != null)
                {
                    if (msgBuffer.MessageType == NetIncomingMessageType.DiscoveryResponse)
                    {
                        bool serverFound = false;
                        ServerInformation serverInfo = new ServerInformation(msgBuffer);
                        foreach (ServerInformation si in serverList)
                            if (si.Equals(serverInfo))
                                serverFound = true;
                        if (!serverFound)
                            serverList.Add(serverInfo);
                    }
                }

                timeTaken += 0.1f;
                Thread.Sleep(100);
            }

            // Discover remote servers.
            try
            {
                string publicList = HttpRequest.Get("http://apps.keithholman.net/plain", null);
                foreach (string s in publicList.Split("\r\n".ToCharArray()))
                {
                    string[] args = s.Split(";".ToCharArray());
                    if (args.Length == 6)
                    {
                        IPAddress serverIp;
                        if (IPAddress.TryParse(args[1], out serverIp) && args[2] == "INFINIMINER")
                        {
                            ServerInformation serverInfo = new ServerInformation(serverIp, args[0], args[5], args[3], args[4]);
                            serverList.Add(serverInfo);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            return serverList;
        }

        public void UpdateNetwork(GameTime gameTime)
        {
            // Update the server with our status.
            timeSinceLastUpdate += gameTime.ElapsedGameTime.TotalSeconds;
            if (timeSinceLastUpdate > 0.05)
            {
                timeSinceLastUpdate = 0;
                if (CurrentStateType == "Infiniminer.States.MainGameState")
                    propertyBag.SendPlayerUpdate();
            }

            // Recieve messages from the server.
            while ((msgBuffer = propertyBag.netClient.ReadMessage()) != null)
            {
                switch (msgBuffer.MessageType)
                {
                    case NetIncomingMessageType.StatusChanged:
                        {
                            if(propertyBag.netClient.ConnectionStatus == NetConnectionStatus.RespondedConnect)
                            {
                                anyPacketsReceived = true;
                            }
                            if (propertyBag.netClient.ConnectionStatus == NetConnectionStatus.Disconnected)
                            {
                                anyPacketsReceived = false;
                                try
                                {
                                    string[] reason = msgBuffer.ReadString().Split(";".ToCharArray());
                                    if (reason.Length < 2 || reason[0] == "VER")
                                        System.Windows.Forms.MessageBox.Show("Error: client/server version incompability!\r\nServer: " + msgBuffer.ReadString() + "\r\nClient: " + Defines.INFINIMINER_VERSION);
                                    else
                                        System.Windows.Forms.MessageBox.Show("Error: you are banned from this server!");
                                }
                                catch { }
                                ChangeState("Infiniminer.States.ServerBrowserState");
                            }
                        }
                        break;        
                    case NetIncomingMessageType.Data:
                        {
                            try
                            {
                                InfiniminerMessage dataType = (InfiniminerMessage)msgBuffer.ReadByte();
                                switch (dataType)
                                {
                                    // TODO: Replace this entire switch statement with a call to the packet registry
                                    case InfiniminerMessage.BlockBulkTransfer:
                                        {
                                            anyPacketsReceived = true;

                                            BlockEngine blockEngine = propertyBag.GetEngine<BlockEngine>("blockEngine");

                                            try
                                            {
                                                //This is either the compression flag or the x coordiante
                                                byte isCompressed = msgBuffer.ReadByte();
                                                byte x;
                                                byte y;

                                                //255 was used because it exceeds the map size - of course, bytes won't work anyway if map sizes are allowed to be this big, so this method is a non-issue
                                                if (isCompressed == 255)
                                                {
                                                    var compressed = msgBuffer.ReadBytes(msgBuffer.LengthBytes - (int)(msgBuffer.Position / 8));
                                                    var compressedstream = new System.IO.MemoryStream(compressed);
                                                    var decompresser = new System.IO.Compression.GZipStream(compressedstream, System.IO.Compression.CompressionMode.Decompress);

                                                    x = (byte)decompresser.ReadByte();
                                                    y = (byte)decompresser.ReadByte();
                                                    propertyBag.mapLoadProgress[x, y] = true;
                                                    for (byte dy = 0; dy < 16; dy++)
                                                        for (byte z = 0; z < 64; z++)
                                                        {
                                                            BlockType blockType = (BlockType)decompresser.ReadByte();
                                                            if (blockType != BlockType.None)
                                                                blockEngine.downloadList[x, y + dy, z] = blockType;
                                                        }
                                                }
                                                else
                                                {
                                                    x = isCompressed;
                                                    y = msgBuffer.ReadByte();
                                                    propertyBag.mapLoadProgress[x, y] = true;
                                                    for (byte dy = 0; dy < 16; dy++)
                                                        for (byte z = 0; z < 64; z++)
                                                        {
                                                            BlockType blockType = (BlockType)msgBuffer.ReadByte();
                                                            if (blockType != BlockType.None)
                                                                blockEngine.downloadList[x, y + dy, z] = blockType;
                                                        }
                                                }
                                                bool downloadComplete = true;
                                                for (x = 0; x < 64; x++)
                                                    for (y = 0; y < 64; y += 16)
                                                        if (propertyBag.mapLoadProgress[x, y] == false)
                                                        {
                                                            downloadComplete = false;
                                                            break;
                                                        }
                                                if (downloadComplete)
                                                {
                                                    ChangeState("Infiniminer.States.TeamSelectionState");
                                                    if (!NoSound)
                                                        MediaPlayer.Stop();
                                                    blockEngine.DownloadComplete();
                                                }
                                            }
                                            catch (Exception e)
                                            {
                                                Console.OpenStandardError();
                                                Console.Error.WriteLine(e.Message);
                                                Console.Error.WriteLine(e.StackTrace);
                                                Console.Error.Close();
                                            }
                                        }
                                        break;

                                    case InfiniminerMessage.SetBeacon:
                                        {
                                            Vector3 position = msgBuffer.ReadVector3();
                                            string text = msgBuffer.ReadString();
                                            PlayerTeam team = (PlayerTeam)msgBuffer.ReadByte();

                                            if (text == "")
                                            {
                                                if (propertyBag.BeaconList.ContainsKey(position))
                                                    propertyBag.BeaconList.Remove(position);
                                            }
                                            else
                                            {
                                                Beacon newBeacon = new Beacon();
                                                newBeacon.ID = text;
                                                newBeacon.Team = team;
                                                propertyBag.BeaconList.Add(position, newBeacon);
                                            }
                                        }
                                        break;

                                    case InfiniminerMessage.TriggerConstructionGunAnimation:
                                        {
                                            propertyBag.PlayerContainer.ConstructionGunAnimation = msgBuffer.ReadFloat();
                                            if (propertyBag.PlayerContainer.ConstructionGunAnimation <= -0.1)
                                                propertyBag.PlaySound(InfiniminerSound.RadarSwitch);
                                        }
                                        break;

                                    case InfiniminerMessage.ResourceUpdate:
                                        {
                                            // ore, cash, weight, max ore, max weight, team ore, red cash, blue cash, all uint
                                            propertyBag.PlayerContainer.PlayerOre = msgBuffer.ReadUInt32();
                                            propertyBag.PlayerContainer.PlayerCash = msgBuffer.ReadUInt32();
                                            propertyBag.PlayerContainer.PlayerWeight = msgBuffer.ReadUInt32();
                                            propertyBag.PlayerContainer.PlayerOreMax = msgBuffer.ReadUInt32();
                                            propertyBag.PlayerContainer.PlayerWeightMax = msgBuffer.ReadUInt32();
                                            propertyBag.teamOre = msgBuffer.ReadUInt32();
                                            propertyBag.teamRedCash = msgBuffer.ReadUInt32();
                                            propertyBag.teamBlueCash = msgBuffer.ReadUInt32();
                                        }
                                        break;

                                    case InfiniminerMessage.BlockSet:
                                        {
                                            BlockEngine blockEngine = propertyBag.GetEngine<BlockEngine>("blockEngine");
                                            // x, y, z, type, all bytes
                                            byte x = msgBuffer.ReadByte();
                                            byte y = msgBuffer.ReadByte();
                                            byte z = msgBuffer.ReadByte();
                                            BlockType blockType = (BlockType)msgBuffer.ReadByte();
                                            if (blockType == BlockType.None)
                                            {
                                                if (blockEngine.BlockAtPoint(new Vector3(x, y, z)) != BlockType.None)
                                                    blockEngine.RemoveBlock(x, y, z);
                                            }
                                            else
                                            {
                                                if (blockEngine.BlockAtPoint(new Vector3(x, y, z)) != BlockType.None)
                                                    blockEngine.RemoveBlock(x, y, z);
                                                blockEngine.AddBlock(x, y, z, blockType);
                                                CheckForStandingInLava();
                                            }
                                        }
                                        break;

                                    case InfiniminerMessage.TriggerExplosion:
                                        {
                                            Vector3 blockPos = msgBuffer.ReadVector3();

                                            // Play the explosion sound.
                                            propertyBag.PlaySound(InfiniminerSound.Explosion, blockPos);

                                            // Create some particles.
                                            propertyBag.GetEngine<ParticleEngine>("particleEngine").CreateExplosionDebris(blockPos);

                                            // Figure out what the effect is.
                                            float distFromExplosive = (blockPos + 0.5f * Vector3.One - propertyBag.PlayerContainer.PlayerPosition).Length();
                                            if (distFromExplosive < 3)
                                                propertyBag.KillPlayer(Defines.deathByExpl);//"WAS KILLED IN AN EXPLOSION!");
                                            else if (distFromExplosive < 8)
                                            {
                                                // If we're not in explosion mode, turn it on with the minimum ammount of shakiness.
                                                if (propertyBag.screenEffect != ScreenEffect.Explosion)
                                                {
                                                    propertyBag.screenEffect = ScreenEffect.Explosion;
                                                    propertyBag.screenEffectCounter = 2;
                                                }
                                                // If this bomb would result in a bigger shake, use its value.
                                                propertyBag.screenEffectCounter = Math.Min(propertyBag.screenEffectCounter, (distFromExplosive - 2) / 5);
                                            }
                                        }
                                        break;

                                    case InfiniminerMessage.PlayerSetTeam:
                                        {
                                            uint playerId = msgBuffer.ReadUInt32();
                                            if (propertyBag.playerList.ContainsKey(playerId))
                                            {
                                                Player player = propertyBag.playerList[playerId];
                                                player.Team = (PlayerTeam)msgBuffer.ReadByte();
                                            }
                                        }
                                        break;

                                    case InfiniminerMessage.PlayerJoined:
                                        {
                                            uint playerId = msgBuffer.ReadUInt32();
                                            string playerName = msgBuffer.ReadString();
                                            bool thisIsMe = msgBuffer.ReadBoolean();
                                            bool playerAlive = msgBuffer.ReadBoolean();
                                            propertyBag.playerList[playerId] = new Player(null, (Game)this);
                                            propertyBag.playerList[playerId].Handle = playerName;
                                            propertyBag.playerList[playerId].ID = playerId;
                                            propertyBag.playerList[playerId].Alive = playerAlive;
                                            propertyBag.playerList[playerId].AltColours = customColours;
                                            propertyBag.playerList[playerId].redTeam = red;
                                            propertyBag.playerList[playerId].blueTeam = blue;
                                            if (thisIsMe)
                                                propertyBag.PlayerContainer.PlayerMyId = playerId;
                                        }
                                        break;

                                    case InfiniminerMessage.PlayerLeft:
                                        {
                                            uint playerId = msgBuffer.ReadUInt32();
                                            if (propertyBag.playerList.ContainsKey(playerId))
                                                propertyBag.playerList.Remove(playerId);
                                        }
                                        break;

                                    case InfiniminerMessage.PlayerDead:
                                        {

                                            uint playerId = msgBuffer.ReadUInt32();
                                            if (propertyBag.playerList.ContainsKey(playerId))
                                            {
                                                Player player = propertyBag.playerList[playerId];
                                                player.Alive = false;
                                                propertyBag.GetEngine<ParticleEngine>("particleEngine").CreateBloodSplatter(player.Position, player.Team == PlayerTeam.Red ? Color.Red : Color.Blue);
                                                if (playerId != propertyBag.PlayerContainer.PlayerMyId)
                                                    propertyBag.PlaySound(InfiniminerSound.Death, player.Position);
                                            }
                                        }
                                        break;

                                    case InfiniminerMessage.PlayerAlive:
                                        {
                                            uint playerId = msgBuffer.ReadUInt32();
                                            if (propertyBag.playerList.ContainsKey(playerId))
                                            {
                                                Player player = propertyBag.playerList[playerId];
                                                player.Alive = true;
                                            }
                                        }
                                        break;

                                    case InfiniminerMessage.PlayerUpdate:
                                        {
                                            uint playerId = msgBuffer.ReadUInt32();
                                            if (propertyBag.playerList.ContainsKey(playerId))
                                            {
                                                Player player = propertyBag.playerList[playerId];
                                                player.UpdatePosition(msgBuffer.ReadVector3(), gameTime.TotalGameTime.TotalSeconds);
                                                player.Heading = msgBuffer.ReadVector3();
                                                player.Tool = (PlayerTools)msgBuffer.ReadByte();
                                                player.UsingTool = msgBuffer.ReadBoolean();
                                                player.Score = (uint)(msgBuffer.ReadUInt16() * 100);
                                            }
                                        }
                                        break;

                                    case InfiniminerMessage.GameOver:
                                        {
                                            propertyBag.teamWinners = (PlayerTeam)msgBuffer.ReadByte();
                                        }
                                        break;

                                    case InfiniminerMessage.ChatMessage:
                                        {
                                            ChatMessageType chatType = (ChatMessageType)msgBuffer.ReadByte();
                                            string chatString = Defines.Sanitize(msgBuffer.ReadString());
                                            //Time to break it up into multiple lines
                                            propertyBag.addChatMessage(chatString, chatType, 10);
                                        }
                                        break;

                                    case InfiniminerMessage.PlayerPing:
                                        {
                                            uint playerId = (uint)msgBuffer.ReadInt32();
                                            if (propertyBag.playerList.ContainsKey(playerId))
                                            {
                                                if (propertyBag.playerList[playerId].Team == propertyBag.PlayerContainer.PlayerTeam)
                                                {
                                                    propertyBag.playerList[playerId].Ping = 1;
                                                    propertyBag.PlaySound(InfiniminerSound.Ping);
                                                }
                                            }
                                        }
                                        break;

                                    case InfiniminerMessage.PlaySound:
                                        {
                                            InfiniminerSound sound = (InfiniminerSound)msgBuffer.ReadByte();
                                            bool hasPosition = msgBuffer.ReadBoolean();
                                            if (hasPosition)
                                            {
                                                Vector3 soundPosition = msgBuffer.ReadVector3();
                                                propertyBag.PlaySound(sound, soundPosition);
                                            }
                                            else
                                                propertyBag.PlaySound(sound);
                                        }
                                        break;
                                }
                            }
                            catch { } //Error in a received message
                        }
                        break;
                }
            }

            // Make sure our network thread actually gets to run.
            Thread.Sleep(1);
        }

        private void CheckForStandingInLava()
        {
            // Copied from TryToMoveTo; responsible for checking if lava has flowed over us.

            BlockEngine blockEngine = propertyBag.GetEngine<BlockEngine>("blockEngine");

            Vector3 movePosition = propertyBag.PlayerContainer.PlayerPosition;
            Vector3 midBodyPoint = movePosition + new Vector3(0, -0.7f, 0);
            Vector3 lowerBodyPoint = movePosition + new Vector3(0, -1.4f, 0);
            BlockType lowerBlock = blockEngine.BlockAtPoint(lowerBodyPoint);
            BlockType midBlock = blockEngine.BlockAtPoint(midBodyPoint);
            BlockType upperBlock = blockEngine.BlockAtPoint(movePosition);
            if (upperBlock == BlockType.Lava || lowerBlock == BlockType.Lava || midBlock == BlockType.Lava)
            {
                propertyBag.KillPlayer(Defines.deathByLava);
            }
        }

        protected override void Initialize()
        {
            graphicsDeviceManager.IsFullScreen = false;
            graphicsDeviceManager.PreferredBackBufferWidth = 1024;
            graphicsDeviceManager.PreferredBackBufferHeight = 768;
            graphicsDeviceManager.PreferredDepthStencilFormat = DepthFormat.Depth24Stencil8;

            //Now moving to DatafileWriter only since it can read and write
            DatafileWriter dataFile = new DatafileWriter("client.config.txt");
            if (dataFile.Data.ContainsKey("width"))
                graphicsDeviceManager.PreferredBackBufferWidth = int.Parse(dataFile.Data["width"], System.Globalization.CultureInfo.InvariantCulture);
            if (dataFile.Data.ContainsKey("height"))
                graphicsDeviceManager.PreferredBackBufferHeight = int.Parse(dataFile.Data["height"], System.Globalization.CultureInfo.InvariantCulture);
            if (dataFile.Data.ContainsKey("fullscreen"))
                graphicsDeviceManager.IsFullScreen = bool.Parse(dataFile.Data["fullscreen"]);
            if (dataFile.Data.ContainsKey("handle"))
                playerHandle = dataFile.Data["handle"];
            if (dataFile.Data.ContainsKey("showfps"))
                DrawFrameRate = bool.Parse(dataFile.Data["showfps"]);
            if (dataFile.Data.ContainsKey("yinvert"))
                InvertMouseYAxis = bool.Parse(dataFile.Data["yinvert"]);
            if (dataFile.Data.ContainsKey("nosound"))
                NoSound = bool.Parse(dataFile.Data["nosound"]);
            if (dataFile.Data.ContainsKey("pretty"))
                RenderPretty = bool.Parse(dataFile.Data["pretty"]);
            if (dataFile.Data.ContainsKey("volume"))
                volumeLevel = Math.Max(0,Math.Min(1,float.Parse(dataFile.Data["volume"], System.Globalization.CultureInfo.InvariantCulture)));
            if (dataFile.Data.ContainsKey("sensitivity"))
                mouseSensitivity=Math.Max(0.001f,Math.Min(0.05f,float.Parse(dataFile.Data["sensitivity"], System.Globalization.CultureInfo.InvariantCulture)/1000f));
            if (dataFile.Data.ContainsKey("red_name"))
                redName = dataFile.Data["red_name"].Trim();
            if (dataFile.Data.ContainsKey("blue_name"))
                blueName = dataFile.Data["blue_name"].Trim();


            if (dataFile.Data.ContainsKey("red"))
            {
                Color temp = new Color();
                string[] data = dataFile.Data["red"].Split(',');
                try
                {
                    temp.R = byte.Parse(data[0].Trim());
                    temp.G = byte.Parse(data[1].Trim());
                    temp.B = byte.Parse(data[2].Trim());
                    temp.A = (byte)255;
                }
                catch {
                    Console.WriteLine("Invalid colour values for red");
                }
                if (temp.A != 0)
                {
                    red = temp;
                    customColours = true;
                }
            }

            if (dataFile.Data.ContainsKey("blue"))
            {
                Color temp = new Color();
                string[] data = dataFile.Data["blue"].Split(',');
                try
                {
                    temp.R = byte.Parse(data[0].Trim());
                    temp.G = byte.Parse(data[1].Trim());
                    temp.B = byte.Parse(data[2].Trim());
                    temp.A = (byte)255;
                }
                catch {
                    Console.WriteLine("Invalid colour values for blue");
                }
                if (temp.A != 0)
                {
                    blue = temp;
                    customColours = true;
                }
            }

            //Now to read the key bindings
            if (!File.Exists("keymap.txt"))
            {
                FileStream temp = File.Create("keymap.txt");
                temp.Close();
                Console.WriteLine("Keymap file does not exist, creating.");
            }
            dataFile = new DatafileWriter("keymap.txt");
            bool anyChanged = false;
            foreach (string key in dataFile.Data.Keys)
            {
                try
                {
                    Buttons button = (Buttons)Enum.Parse(typeof(Buttons),dataFile.Data[key],true);
                    if (Enum.IsDefined(typeof(Buttons), button))
                    {
                        if (keyBinds.BindKey(button, key, true))
                        {
                            anyChanged = true;
                        }
                    }
                    else
                    {
                        Console.WriteLine("Enum not defined for " + dataFile.Data[key] + ".");
                    }
                } catch { }
            }

            //If no keys are bound in this manner then create the default set
            if (!anyChanged)
            {
                keyBinds.CreateDefaultSet();
                keyBinds.SaveBinds(dataFile, "keymap.txt");
                Console.WriteLine("Creating default keymap...");
            }
            graphicsDeviceManager.ApplyChanges();
            base.Initialize();
        }

        protected override void Update(GameTime gameTime)
        {
            base.Update(gameTime);
        }

        protected override void OnExiting(object sender, EventArgs args)
        {
            propertyBag.netClient.Shutdown("Client exiting.");
            
            base.OnExiting(sender, args);
        }

        public void ResetPropertyBag()
        {
            if (propertyBag != null)
                propertyBag.netClient.Shutdown("");

            propertyBag = new Infiniminer.PropertyBag(this);
            propertyBag.SettingsContainer.PlayerHandle = playerHandle;
            propertyBag.SettingsContainer.VolumeLevel = volumeLevel;
            propertyBag.SettingsContainer.MouseSensitivity = mouseSensitivity;
            propertyBag.keyBinds = keyBinds;
            propertyBag.blue = blue;
            propertyBag.red = red;
            propertyBag.blueName = blueName;
            propertyBag.redName = redName;
            msgBuffer = null;
        }

        protected override void LoadContent()
        {
            // Initialize the property bag.
            ResetPropertyBag();

            // Make sure to initialize our brand spanking new PlexiglassClientManager.
            InitializePlexiglassInstance();

            // Set the initial state to team selection
            ChangeState("Infiniminer.States.TitleState");

            // Play the title music.
            if (!NoSound)
            {
                songTitle = Content.Load<Song>("song_title");
                MediaPlayer.Play(songTitle);
                MediaPlayer.Volume = propertyBag.SettingsContainer.VolumeLevel;
            }
        }

        /// ========================= BEGIN PLEXIGLASS FUNCTIONS ========================= \\\
        
        protected void InitializePlexiglassInstance()
        {
            plexiglassInstance = new PlexiglassClientManager();
            plexiglassInstance.InitializeContentManager();
            plexiglassInstance.InitializePacketRegistry(this.propertyBag, this);
        }
    }
}
