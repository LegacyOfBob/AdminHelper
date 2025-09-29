using System.Linq; // Required for LINQ extensions like Select and ToArray
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace AdminHelper
{
    // =========================================================================================
    // 1. NETWORK PACKET
    // This is the data structure sent from the client to the server.
    // =========================================================================================
    public class AdminCommandPacket
    {
        public string CommandText { get; set; }
    }

    // =========================================================================================
    // 2. MAIN MOD SYSTEM
    // Handles registration, keybinds, and network listeners.
    // =========================================================================================
    public class AdminToolModSystem : ModSystem
    {
        private ICoreAPI api;
        private ICoreClientAPI capi;
        private ICoreServerAPI sapi;

        private AdminCommandDialog dialog;
        private const string ChannelName = "AdminToolChannel";
        private const string GuiKeybind = "admincmdgui";

        public override void Start(ICoreAPI api)
        {
            this.api = api;
            api.Logger.Notification("[AdminTool] Mod system loaded.");
        }

        public override void StartClientSide(ICoreClientAPI capi)
        {
            this.capi = capi;

            // Register the custom network channel for client-server communication
            capi.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<AdminCommandPacket>();

            // Instantiate and register the GUI dialog
            dialog = new AdminCommandDialog(capi, this);
            capi.Gui.RegisterDialog(dialog);

            // Register a keybinding to open the GUI
            capi.Input.RegisterHotKey(GuiKeybind, "Admin Command GUI", GlKeys.K, HotkeyType.GUIOrOtherControls, ctrlPressed: true);
            capi.Input.SetHotKeyHandler(GuiKeybind, ToggleDialog);

            capi.Logger.Notification("[AdminTool] Client loaded. Press Ctrl+K to open GUI.");
        }

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            this.sapi = sapi;

            // Register the custom network channel and add a handler for incoming packets
            sapi.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<AdminCommandPacket>()
                .SetMessageHandler<AdminCommandPacket>(OnAdminCommandReceived);

            sapi.Logger.Notification("[AdminTool] Server loaded. Ready to receive commands.");
        }

        /// Toggles the visibility of the Admin Command GUI, ensuring it's setup correctly before opening.
        private bool ToggleDialog(KeyCombination kc)
        {
            if (dialog != null)
            {
                if (dialog.IsOpened())
                {
                    dialog.TryClose();
                }
                else
                {
                    // FIX: IClientPlayer does not have Capabilities, use HasPrivilege("setgamemode") instead
                    if (capi.World.Player.HasPrivilege("setgamemode"))
                    {
                        // Setup data (player lists, waypoints) just before opening
                        dialog.SetupAndOpen();
                    }
                    else
                    {
                        capi.ShowChatMessage("You do not appear to have sufficient client-side privileges to use this tool.");
                    }
                }
            }
            return true;
        }

        /// SERVER HANDLER: Receives the command packet from the client and attempts to execute it.
        private void OnAdminCommandReceived(IServerPlayer player, AdminCommandPacket packet)
        {
            // IMPORTANT: The server MUST verify permissions!
            if (!player.HasPrivilege("admin") && !player.HasPrivilege("setgamemode"))
            {
                api.Logger.Warning($"Player {player.PlayerName} (UID: {player.PlayerUID}) tried to execute an admin command without privileges: {packet.CommandText}");
                player.SendMessage(GlobalConstants.GeneralChatGroup, "You must be a server operator to use this tool.", EnumChatType.OwnMessage);
                return;
            }

            string command = packet.CommandText;
            api.Logger.Event($"Executing command for {player.PlayerName}: {command}");

            // Commands in Vintage Story start with a '.', we need to remove it if present.
            string trimmedCommand = command.StartsWith(".") ? command.Substring(1) : command;

            // FIX: Use ExecuteUnparsed with correct arguments
            var args = new TextCommandCallingArgs
            {
                Caller = (Caller)player,
                RawArgs = new CmdArgs(trimmedCommand.Split(' ')),
                // Optionally set other properties as needed, e.g. World, Command, etc.
            };
            bool success = false;
            sapi.ChatCommands.ExecuteUnparsed(trimmedCommand, args, result => success = result.Status == EnumCommandStatus.Success);

            if (success)
            {
                player.SendMessage(GlobalConstants.GeneralChatGroup, $"Executed command: .{trimmedCommand}", EnumChatType.OwnMessage);
            }
            else
            {
                player.SendMessage(GlobalConstants.GeneralChatGroup, $"Failed to execute command: .{trimmedCommand}", EnumChatType.OwnMessage);
            }
        }

        /// CLIENT METHOD: Sends the command text to the server. Called by the GUI's execute button.
        public void SendCommandToServer(string command)
        {
            if (capi != null)
            {
                AdminCommandPacket packet = new AdminCommandPacket { CommandText = command };
                capi.Network.GetChannel(ChannelName).SendPacket(packet);
            }
        }
    }

    // =========================================================================================
    // 3. GUI DIALOG
    // The visual component where the user enters the command.
    // =========================================================================================
    public class AdminCommandDialog : GuiDialog
    {
        private AdminToolModSystem modSystem;

        public ElementBounds ComposeG { get; private set; }

        private GuiElementTextInput commandInput;
        private GuiElementTextInput coordInput;
        private GuiElementTextDropdown targetPlayerDropdown;
        private GuiElementTextDropdown destPlayerDropdown;
        private GuiElementTextDropdown waypointDropdown;

        private string selectedTargetMode = "self"; // "self", "lookingat", "player"
        private string selectedDestinationMode = "lookingat"; // "self", "lookingat", "coords", "player", "waypoint"

        // Element IDs for state management
        private const string ID_TARGET_PLAYER_DD = "targetPlayerDD";
        private const string ID_DEST_PLAYER_DD = "destPlayerDD";
        private const string ID_WAYPOINT_DD = "waypointDD";
        private const string ID_COORD_INPUT = "coordInput";

        public override string ToggleKeyCombinationCode => "admincmdgui";

        public AdminCommandDialog(ICoreClientAPI capi, AdminToolModSystem modSystem) : base(capi)
        {
            this.modSystem = modSystem;
            ComposeG = ComposeGui();
        }

        /// Handles populating lists before opening the dialog.
        public void SetupAndOpen()
        {
            UpdatePlayerDropdowns();
            UpdateWaypointDropdown();
            UpdateConditionalElementsVisibility();
            TryOpen();
        }

        // Helper function to create the GUI structure and elements
        private ElementBounds ComposeGui()
        {
            // --- 1. Bounds Setup ---
            // Set the overall size and position of the dialog (larger now)
            ElementBounds dialogBounds = ElementBounds.Fixed(EnumDialogArea.CenterFixed, 0, 0, 800, 450)
                .WithAlignment(EnumDialogArea.CenterFixed);

            // Set the inner bounds
            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(20, 20);

            // Teleport Control Area (2 columns)
            ElementBounds teleportArea = ElementBounds.Percentual(0, 0, 1, 0.6f).WithParent(bgBounds);
            ElementBounds leftColumn = ElementBounds.Percentual(0, 0, 0.48f, 1).WithParent(teleportArea).FixedGrow(10, 0);
            ElementBounds rightColumn = ElementBounds.Percentual(0.52f, 0, 0.48f, 1).WithParent(teleportArea);

            // Command Input Area (Bottom)
            ElementBounds commandArea = ElementBounds.Percentual(0, 0.7f, 1, 0.3f).WithParent(bgBounds);
            ElementBounds labelBounds = ElementBounds.Fixed(0, 0, 400, 20).WithParent(commandArea);
            ElementBounds inputBounds = labelBounds.FlatCopy().FixedUnder(labelBounds, 5).WithFixedWidth(680);
            ElementBounds buttonBounds = ElementBounds.Fixed(0, 0, 80, 30).FixedRightOf(inputBounds, 10);

            // --- 2. Teleport Section Content ---
            float rowHeight = 30;

            // --- LEFT COLUMN: TARGET (WHO) ---
            GuiComposer compo = capi.Gui.CreateCompo("admincommanddialog", dialogBounds)
                .AddDialogTitleBar("Admin Command Tool", OnTitleBarClose)
                .AddDialogBG(bgBounds, true);

            compo.AddStaticText("1. Teleport Target (Who to move):", CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, 0, 300, 20).WithFixedHeight(20).WithParent(leftColumn));

            // Target: Self
            ElementBounds targetSelf = ElementBounds.Fixed(0, 0, 100, rowHeight).FixedUnder(leftColumn.ChildBounds[0], 5);
            compo.AddButton("Self", () => OnTargetSelected("self"), targetSelf, EnumButtonStyle.Normal, "targetSelf");
            var targetSelfBtn = compo.GetElement("targetSelf");
            if (targetSelfBtn is Vintagestory.API.Client.GuiElementTextButton btnSelf) btnSelf.Enabled = selectedTargetMode == "self";

            // Target: Looking At (Target Block)
            ElementBounds targetLook = targetSelf.CopyOnlySize().FixedRightOf(targetSelf, 5);
            compo.AddButton("Looking At", () => OnTargetSelected("lookingat"), targetLook, EnumButtonStyle.Normal, "targetLookingat");
            var targetLookingatBtn = compo.GetElement("targetLookingat");
            if (targetLookingatBtn is Vintagestory.API.Client.GuiElementTextButton btnLookingat) btnLookingat.Enabled = selectedTargetMode == "lookingat";

            // Target: Player
            ElementBounds targetPlayerBtn = targetLook.CopyOnlySize().FixedRightOf(targetLook, 5);
            compo.AddButton("Player", () => OnTargetSelected("player"), targetPlayerBtn, EnumButtonStyle.Normal, "targetPlayer");
            var targetPlayerBtnElem = compo.GetElement("targetPlayer");
            if (targetPlayerBtnElem is Vintagestory.API.Client.GuiElementTextButton btnPlayer) btnPlayer.Enabled = selectedTargetMode == "player";

            // Player Dropdown for Target (Initially hidden)
            ElementBounds targetPlayerDD = targetSelf.CopyOnlySize().FixedUnder(targetSelf, 5);
            compo.AddDropDown(new string[0], new string[0], 0, OnTargetPlayerSelected, targetPlayerDD);


            // --- RIGHT COLUMN: DESTINATION (WHERE TO GO) ---
            compo.AddStaticText("2. Teleport Destination (Where to go):", CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, 0, 300, 20).WithFixedHeight(20).WithParent(rightColumn));

            ElementBounds destRow1 = ElementBounds.Fixed(0, 0, 100, rowHeight).FixedUnder(rightColumn.ChildBounds[0], 5);
            compo.AddButton("Looking At", () => OnDestinationSelected("lookingat"), destRow1, EnumButtonStyle.Normal, "destLookingat");
            var destLookingatBtn = compo.GetElement("destLookingat");
            if (destLookingatBtn is Vintagestory.API.Client.GuiElementTextButton btnDestLookingat) btnDestLookingat.Enabled = selectedDestinationMode == "lookingat";

            ElementBounds destSelf = destRow1.CopyOnlySize().FixedRightOf(destRow1, 5);
            compo.AddButton("Self", () => OnDestinationSelected("self"), destSelf, EnumButtonStyle.Normal, "destSelf");
            var destSelfBtn = compo.GetElement("destSelf");
            if (destSelfBtn is Vintagestory.API.Client.GuiElementTextButton btnDestSelf) btnDestSelf.Enabled = selectedDestinationMode == "self";

            ElementBounds destPlayer = destSelf.CopyOnlySize().FixedRightOf(destSelf, 5);
            compo.AddButton("Player", () => OnDestinationSelected("player"), destPlayer, EnumButtonStyle.Normal, "destPlayer");
            var destPlayerBtnElem = compo.GetElement("destPlayer");
            if (destPlayerBtnElem is Vintagestory.API.Client.GuiElementTextButton btnDestPlayer) btnDestPlayer.Enabled = selectedDestinationMode == "player";

            ElementBounds destRow2 = destRow1.CopyOnlySize().FixedUnder(destRow1, 5);
            ElementBounds destCoords = ElementBounds.Fixed(0, 0, 100, rowHeight);
            compo.AddButton("Coordinates", () => OnDestinationSelected("coords"), destCoords, EnumButtonStyle.Normal, "destCoords");
            var destCoordsBtn = compo.GetElement("destCoords");
            if (destCoordsBtn is Vintagestory.API.Client.GuiElementTextButton btnDestCoords) btnDestCoords.Enabled = selectedDestinationMode == "coords";

            ElementBounds destWaypoint = destCoords.CopyOnlySize().FixedRightOf(destCoords, 5);
            compo.AddButton("Waypoint", () => OnDestinationSelected("waypoint"), destWaypoint, EnumButtonStyle.Normal, "destWaypoint");
            var destWaypointBtn = compo.GetElement("destWaypoint");
            if (destWaypointBtn is Vintagestory.API.Client.GuiElementTextButton btnDestWaypoint) btnDestWaypoint.Enabled = selectedDestinationMode == "waypoint";

            // Conditional Inputs Area (Below Destination Buttons)

            // Player Dropdown for Destination (Initially hidden)
            ElementBounds destPlayerDD = destRow2.CopyOnlySize().FixedUnder(destRow2, 5);
            compo.AddDropDown(new string[0], new string[0], 0, OnDestPlayerSelected, destPlayerDD);

            // Waypoint Dropdown (Initially hidden)
            ElementBounds waypointDD = destPlayerDD.CopyOnlySize();
            compo.AddDropDown(new string[0], new string[0], 0, OnWaypointSelected, waypointDD);

            // Coordinates Input (Initially hidden)
            ElementBounds coordInputBounds = destPlayerDD.CopyOnlySize();
            coordInput = compo.AddTextInput(coordInputBounds, OnCoordInputChanged, ID_COORD_INPUT)
                 .SetPlaceHolderText("X Y Z (e.g., 100 70 100)");

            // --- GENERATE BUTTON ---
            ElementBounds generateButtonBounds = ElementBounds.Fixed(0, 0, 200, 30)
                .FixedUnder(teleportArea, 20).WithAlignment(EnumDialogArea.CenterHorizontal);
            compo.AddButton("Generate Teleport Command", () => OnGenerateCommand(), generateButtonBounds, EnumButtonStyle.Normal, EnumTextOrientation.Left);

            // --- 3. Base Command Input Section ---

            compo.AddStaticText("Final Command:", CairoFont.WhiteSmallish(), ElementBounds.Fixed(0, 0, 400, 20).WithParent(commandArea));

            // The text input field
            compo.AddTextInput(ElementBounds.Fixed(0, 0, 680, 30).FixedUnder(labelBounds, 5), OnTextInputChanged, out commandInput, "commandInput")
                .Set and; // Focus on the input field when opened

            // The execute button
            compo.AddButton("Execute", () => OnExecuteButtonPressed(), ElementBounds.Fixed(0, 0, 80, 30).FixedRightOf(inputBounds, 10), EnumButtonStyle.Normal, EnumTextOrientation.Left);

            // Final composition and element retrieval
            GuiComposer = compo.Compose();

            // Retrieve elements for runtime modification (dropdowns, inputs)
            targetPlayerDropdown = GuiComposer.GetElement(AdminCommandDialog.ID_TARGET_PLAYER_DD);
            destPlayerDropdown = GuiComposer.GetElement(AdminCommandDialog.ID_DEST_PLAYER_DD);
            waypointDropdown = GuiComposer.GetElement(AdminCommandDialog.ID_WAYPOINT_DD);
            coordInput = GuiComposer.GetElement(AdminCommandDialog.ID_COORD_INPUT);

            // Set initial visibility
            UpdateConditionalElementsVisibility();

            commandInput.SetValue("");
            return dialogBounds;
        }

        private void OnTitleBarClose() => TryClose();

        private void OnTextInputChanged(string text) { /* Optional validation */ }
        private void OnCoordInputChanged(string text) { /* Optional validation */ }

        // --- Target and Destination Selection Handlers ---

        private bool OnTargetSelected(string mode)
        {
            selectedTargetMode = mode;
            (GuiComposer.GetElement("targetSelf") as GuiElementButton).Enabled = mode == "self";
            (GuiComposer.GetElement("targetLookingat") as GuiElementButton).Enabled = mode == "lookingat";
            (GuiComposer.GetElement("targetPlayer") as GuiElementButton).Enabled = mode == "player";

            UpdateConditionalElementsVisibility();
            return true;
        }

        private bool OnDestinationSelected(string mode)
        {
            selectedDestinationMode = mode;
            (GuiComposer.GetElement("destLookingat") as GuiElementButton).Enabled = mode == "lookingat";
            (GuiComposer.GetElement("destSelf") as GuiElementButton).Enabled = mode == "self";
            (GuiComposer.GetElement("destPlayer") as GuiElementButton).Enabled = mode == "player";
            (GuiComposer.GetElement("destCoords") as GuiElementButton).Enabled = mode == "coords";
            (GuiComposer.GetElement("destWaypoint") as GuiElementButton).Enabled = mode == "waypoint";

            UpdateConditionalElementsVisibility();
            return true;
        }

        private void OnTargetPlayerSelected(string text, bool selected)
        {
            // The value is auto-updated in the dropdown element
        }

        private void OnDestPlayerSelected(string text, bool selected)
        {
            // The value is auto-updated in the dropdown element
        }

        private void OnWaypointSelected(string text, bool selected)
        {
            // The value is auto-updated in the dropdown element
        }

        // --- Data Population and Visibility ---

        private void UpdatePlayerDropdowns()
        {
            string[] playerNames = capi.World.AllOnlinePlayers.Select(p => p.PlayerName).ToArray();

            targetPlayerDropdown.SetItems(playerNames);
            if (playerNames.Length > 0) targetPlayerDropdown.SetSelectedText(playerNames[0]);

            destPlayerDropdown.SetItems(playerNames);
            if (playerNames.Length > 0) destPlayerDropdown.SetSelectedText(playerNames[0]);
        }

        private void UpdateWaypointDropdown()
        {
            string[] waypointTitles = capi.World.Player.WorldData.Waypoints.Select(w => w.Title).ToArray();
            waypointDropdown.SetItems(waypointTitles);
            if (waypointTitles.Length > 0) waypointDropdown.SetSelectedText(waypointTitles[0]);
        }

        private void UpdateConditionalElementsVisibility()
        {
            // Target Player Dropdown
            bool showTargetPlayer = selectedTargetMode == "player";
            targetPlayerDropdown.SetVisible(showTargetPlayer);

            // Destination Conditional Elements
            destPlayerDropdown.SetVisible(selectedDestinationMode == "player");
            waypointDropdown.SetVisible(selectedDestinationMode == "waypoint");
            coordInput.SetVisible(selectedDestinationMode == "coords");

            // If the dropdowns are invisible, their corresponding selection doesn't matter (prevents errors)
        }

        // --- Command Generation and Execution ---

        private bool OnGenerateCommand()
        {
            string targetArg = GetTeleportTargetArgument();
            string destinationArg = GetTeleportDestinationArgument();

            if (targetArg == null || destinationArg == null)
            {
                capi.ShowChatMessage("Error: Could not resolve all teleport parameters. Check input fields.");
                return true;
            }

            // Command format: teleport [target] [destination]
            commandInput.SetValue($"teleport {targetArg} {destinationArg}");
            return true;
        }

        private string GetTeleportTargetArgument()
        {
            IClientPlayer player = capi.World.Player;

            switch (selectedTargetMode)
            {
                case "self":
                    return player.PlayerName;
                case "lookingat":
                    // The server's 'teleport' command supports looking at a block, so we pass the player's name 
                    // and let the server resolve the target block for the destination. 
                    // Since 'teleport' takes two args (target and destination), and we want to move the player 
                    // to the destination, we treat the 'target' as the player's name.
                    return player.PlayerName;
                case "player":
                    return targetPlayerDropdown.Get and;
                default:
                    return null;
            }
        }

        private string GetTeleportDestinationArgument()
        {
            IClientPlayer player = capi.World.Player;

            switch (selectedDestinationMode)
            {
                case "self":
                    // Teleport to self (no-op, but valid syntax)
                    return player.Entity.Pos.AsBlockPos.X + " " + player.Entity.Pos.AsBlockPos.Y + " " + player.Entity.Pos.AsBlockPos.Z;
                case "lookingat":
                    // The server's 'teleport' command supports "block" as a destination.
                    TargetedBlock targetedBlock = player.CurrentBlockSelection;
                    if (targetedBlock != null)
                    {
                        // +1 to Y to stand on top of the block
                        BlockPos pos = targetedBlock.Position;
                        return $"{pos.X} {pos.Y + 1} {pos.Z}";
                    }
                    else
                    {
                        capi.ShowChatMessage("Destination Error: Must be looking at a block to use 'Looking At'.");
                        return null;
                    }
                case "coords":
                    string coords = coordInput.Get and;
                    if (string.IsNullOrWhiteSpace(coords))
                    {
                        capi.ShowChatMessage("Destination Error: Coordinates cannot be empty.");
                        return null;
                    }
                    return coords; // Assume user input is valid: "X Y Z"
                case "player":
                    return destPlayerDropdown.Get and;
                case "waypoint":
                    string waypointTitle = waypointDropdown.Get and;
                    Waypoint wp = player.WorldData.Waypoints.FirstOrDefault(w => w.Title == waypointTitle);
                    if (wp != null)
                    {
                        // Add 1 to Y to stand on top of the block
                        return $"{wp.Position.X} {wp.Position.Y + 1} {wp.Position.Z}";
                    }
                    else
                    {
                        capi.ShowChatMessage($"Destination Error: Waypoint '{waypointTitle}' not found.");
                        return null;
                    }
                default:
                    return null;
            }
        }

        private bool OnExecuteButtonPressed()
        {
            string command = commandInput.Get and;

            if (string.IsNullOrWhiteSpace(command))
            {
                capi.ShowChatMessage("Please enter a command to execute.");
                return true; // Keep dialog open
            }

            modSystem.SendCommandToServer(command);

            // Clear the input and close the dialog
            commandInput.SetValue("");
            TryClose();
            return true;
        }

        public override bool PrefersOwnInput() => true;
    }

    internal class GuiElementTextDropdown
    {
    }
}
