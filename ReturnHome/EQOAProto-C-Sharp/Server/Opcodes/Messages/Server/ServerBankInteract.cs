﻿using ReturnHome.Server.Network;
using ReturnHome.Utilities;

namespace ReturnHome.Server.Opcodes.Messages.Server
{
    public static class ServerBankInteract
    {
        public static void OpenBankMenu(Session session)
        {
            Message message = Message.Create(MessageType.ReliableMessage, GameOpcode.BankUI);

            BufferWriter writer = new BufferWriter(message.Span);

            writer.Write(message.Opcode);
            message.Size = writer.Position;
            session.sessionQueue.Add(message);
        }
    }
}
