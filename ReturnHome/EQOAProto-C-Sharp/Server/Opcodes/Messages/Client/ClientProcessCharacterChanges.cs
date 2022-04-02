﻿using ReturnHome.Database.SQL;
using ReturnHome.Server.EntityObject.Player;
using ReturnHome.Server.Network;
using ReturnHome.Server.Network.Managers;
using ReturnHome.Utilities;

namespace ReturnHome.Server.Opcodes.Messages.Client
{
    class ClientProcessCharacterChanges
    {
        public static void ProcessCharacterChanges(Session MySession, PacketMessage ClientPacket)
        {
            BufferReader reader = new(ClientPacket.Data.Span);

            //Retrieve CharacterID from client
            int ServerID = reader.Read<int>();
            int FaceOption = reader.Read<int>();
            int HairStyle = reader.Read<int>();
            int HairLength = reader.Read<int>();
            int HairColor = reader.Read<int>();

            CharacterSQL cSQL = new();
            //Query Character
            Character MyCharacter = cSQL.AcquireCharacter(MySession, ServerID);
            cSQL.CloseConnection();
            try
            {
                SessionManager.CreateMemoryDumpSession(MySession, MyCharacter);
            }
            catch
            {
                Logger.Err($"Unable to create Memory Dump Session for {MySession.SessionID} : {MyCharacter.CharName}");
            }
        }
    }
}
