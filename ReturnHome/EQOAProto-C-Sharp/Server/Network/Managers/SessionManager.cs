﻿using System;
using System.Net;
using System.Threading.Tasks;
using System.Linq;

using ReturnHome.Utilities;
using ReturnHome.Opcodes;
using ReturnHome.Server.Managers;
using ReturnHome.Server.EntityObject.Player;

namespace ReturnHome.Server.Network.Managers
{
    public static class SessionManager
    {
        ///Our IDUP Starter for now
        private static uint InstanceIDUpStarter = 220760;

        ///This is our sessionList
        public static readonly ConcurrentHashSet<Session> SessionHash = new ConcurrentHashSet<Session>();

        /// <summary>
        /// Handles packets from clients, creating/removing sessions, or sending established connections data to process
        /// </summary>
        public static void ProcessPacket(ServerListener listener, ClientPacket packet, IPEndPoint ClientIPEndPoint)
        ///public static void ProcessSession(List<byte> myPacket, bool NewSession)
        {
			Session ClientSession;

            //Remove session
            if (packet.Header.CancelSession)
            {
                //Add a method here to save session data and character etc, whatever is applicable.
                //Attempt to remove from serverlisting for now
                findSession(ClientIPEndPoint, packet.Header.InstanceID, out ClientSession);
                ServerListManager.RemoveSession(ClientSession);
                if (SessionHash.TryRemove(ClientSession))
                    Logger.Info("Session Successfully removed from Session List");
                return;
            }

            //Create a new session
            if(packet.Header.NewInstance)
			{
                // Create New Session
                ClientSession = new Session(listener, ClientIPEndPoint, packet.Header.InstanceID, packet.Header.SessionID, packet.Header.ClientEndPoint, listener.serverEndPoint, false);

                //Try adding session to hashset
                if (SessionHash.TryAdd(ClientSession))
                {
                    Logger.Info($"{ClientSession.ClientEndpoint.ToString("X")}: Processing new session");
                    ClientSession.SessionAck = true;
                    //Success, keep processing data
                    ClientSession.ProcessPacket(packet);
                }
			}

            else
            {
				//Find session, if it returns true, outputs session
                if (findSession(ClientIPEndPoint, out ClientSession))
                {
					//Checks if IP/Port matches expected session to incoming packet
					//This might not be needed?
                    if (ClientSession.MyIPEndPoint.Equals(ClientIPEndPoint))
                        ClientSession.ProcessPacket(packet);

                    else
                    {
                        //Somehow got the wrong session? Def. Needs a log to notate this
                        ClientSession = null;
                        Logger.Info($"Session for Id {packet.Header.ClientEndPoint} has IP {ClientSession.MyIPEndPoint} but packet has IP {ClientIPEndPoint}");
                    }
                }

                else
                {
                    Logger.Info($"Unsolicited Packet from {ClientIPEndPoint} with Id { packet.Header.ClientEndPoint}");
                }
            }
        }

        /// <summary>
        /// Finds a session and returns it
        /// </summary>
        public static bool findSession(IPEndPoint ClientIPEndPoint, uint InstanceID, out Session actualSession)
        {
            foreach (Session ClientSession in SessionHash)
            {
                if (ClientSession.MyIPEndPoint.Equals(ClientIPEndPoint) && ClientSession.InstanceID == InstanceID)
                {
                    actualSession = ClientSession;
                    return true;
                }
            }

            //Need logging to indicate actual session was not found
            actualSession = default;
            return false;
        }

        //Have two seperate FindSession functions to handle the times instanceID is not in header, may be a better way to do this.
        public static bool findSession(IPEndPoint ClientIPEndPoint, out Session actualSession)
        {
            foreach (Session ClientSession in SessionHash)
            {
                if (ClientSession.MyIPEndPoint.Equals(ClientIPEndPoint))
                {
                    actualSession = ClientSession;
                    return true;
                }
            }

            //Need logging to indicate actual session was not found
            actualSession = default;
            return false;
        }

        public static uint ObtainIDUp()
        {
            ///Eventually would need some checks to make sure it isn't taken in bigger scale
            uint NewID = InstanceIDUpStarter;
            ///Increment by 2 every pull
            ///Client will Transition into InstanceIDUpStarter + 1
            InstanceIDUpStarter += 2;

            return NewID;
        }

        public static void CreateMasterSession(Session MySession)
        {
            Session NewMasterSession = new Session(MySession.rdpCommOut._listener, MySession.MyIPEndPoint, DNP3Creation.DNP3Session(), ObtainIDUp(), MySession.rdpCommIn.clientID, MySession.rdpCommIn.serverID, true);
            NewMasterSession.AccountID = MySession.AccountID;
            NewMasterSession.Instance = true;

            if (SessionHash.TryAdd(NewMasterSession))
            {
                //Start client contact here
                GenerateClientContact(NewMasterSession);
            }

        }

        public static void CreateMemoryDumpSession(Session MySession, Character MyCharacter)
        {
            //Start new session 
            Session NewMasterSession = new Session(MySession.rdpCommOut._listener, MySession.MyIPEndPoint, DNP3Creation.DNP3Session(), MySession.SessionID + 1, MySession.rdpCommIn.clientID, MySession.rdpCommIn.serverID, true);
            NewMasterSession.Instance = true;
            NewMasterSession.AccountID = MySession.AccountID;
            NewMasterSession.MyCharacter = MyCharacter;
            NewMasterSession.MyCharacter.characterSession = NewMasterSession;
            NewMasterSession.MyCharacter.ObjectID = NewMasterSession.SessionID;

            if (SessionHash.TryAdd(NewMasterSession))
            {
                Console.WriteLine("Ready For Memory Dump");
                ProcessOpcode.ProcessMemoryDump(NewMasterSession);
            }
        }

        ///Used when starting a master session with client.
        public static void GenerateClientContact(Session MySession)
        {
            int offset = 0;
            Memory<byte> temp1 = new byte[6];
            Span<byte> Message = temp1.Span;
            Message.Write(BitConverter.GetBytes((ushort)GameOpcode.Camera1), ref offset);
            Message.Write(new byte[] { 0x03, 0x00, 0x00, 0x00 }, ref offset);
            ///Handles packing message into outgoing packet
            SessionQueueMessages.PackMessage(MySession, temp1, MessageOpcodeTypes.ShortReliableMessage);

            offset = 0;
            Memory<byte> temp2 = new byte[6];
            Span<byte> Message2 = temp2.Span;
            Message2.Write(BitConverter.GetBytes((ushort)GameOpcode.Camera2), ref offset);
            Message2.Write(new byte[] { 0x1B, 0x00, 0x00, 0x00 }, ref offset);
            ///Handles packing message into outgoing packet
            SessionQueueMessages.PackMessage(MySession, temp2, MessageOpcodeTypes.ShortReliableMessage);

        }
		
		/// <summary>
        /// Dispatches all outgoing messages.<para />
        /// Removes dead sessions.
        /// </summary>
        public static int BeginSessionWork()
        {
            int sessionCount = 0;

            //Should push client object update directly to character if needed
            Parallel.ForEach(SessionHash, s => s?.UpdateClientObject());

            MapManager.BulkAddPlayers();
            MapManager.QueryObjectsForDistribution();

            // The session tick outbound processes pending actions and handles outgoing messages
            Parallel.ForEach(SessionHash, s => s?.TickOutbound());

            // Removes sessions in the NetworkTimeout state, including sessions that have reached a timeout limit.
            foreach (var session in SessionHash.Where(k => !Equals(null, k)))
            {
                if (session.PendingTermination)
                {
                    Console.WriteLine("Dropping Session on server end");
                    session.DropSession();
                }

                sessionCount++;
            }
			
            return sessionCount;
        }
		
    }
}
