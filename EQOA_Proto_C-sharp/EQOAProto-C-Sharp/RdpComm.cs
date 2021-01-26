﻿using EQLogger;
using EQOAProto;
using EQOASQL;
using OpcodeOperations;
using Opcodes;
using SessManager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using Utility;
using Characters;
using Sessions;
using Unreliable;

namespace RdpComm
{
    /// <summary>
    /// This is user to start processing incoming and outgoing packets
    /// </summary>
    class RdpCommIn
    {
        private static bool Session_Ack = false;
        private static bool RDP_Report = false;
        private static bool Message_Bundle = false;

        public static void ProcessBundle(Session MySession, List<byte> MyPacket)
        {
            
            ///Grab our bundle type
            sbyte BundleType = (sbyte)MyPacket[0];
            Logger.Info($"BundleType is {BundleType}");

            ///Remove read byte
            MyPacket.RemoveRange(0, 1);

            ///Perform a check to find what switch statement is true
            switch (BundleType)
            {
                case BundleOpcode.ProcessAll:
                    Session_Ack = true;
                    RDP_Report = true;
                    ///Message_Bundle = true;

                    Logger.Info("Processing Session Ack, Rdp Report and Message Bundle");
                    break;

                case BundleOpcode.NewProcessReport:
                case BundleOpcode.ProcessReport:
                    RDP_Report = true;
                    Logger.Info("Processing Rdp Report");
                    break;

                case BundleOpcode.NewProcessMessages:
                case BundleOpcode.ProcessMessages:
                    Message_Bundle = true;
                    Logger.Info("Processing Message Bundle");
                    break;

                case BundleOpcode.ProcessMessageAndReport:
                    Message_Bundle = true;
                    RDP_Report = true;
                    Logger.Info("Processing Messages and Reports");
                    break;

                default:
                    Logger.Err("Unable to identify Bundle Type");
                    break;
            }

            /*
             Should this be placed within the switch? May look cleaner/less code
             */

            if (Session_Ack == true)
            {
                ///Process Session Acks here
                ProcessSessionAck(MySession, MyPacket);

            }

            if (RDP_Report == true)
            {
                ///Process RDP Report
                ProcessRdpReport(MySession, MyPacket);
            }

            if (Message_Bundle == true)
            {
                //Read client bundle here. Accept client bundle as is because packets could be lost or dropped, client bundle# should not be "tracked"
                //considerations could be to track it for possible drop/lost rates
                MySession.clientBundleNumber = (ushort)(MyPacket[1] << 8 | MyPacket[0]);

                ///Remove read byte
                MyPacket.RemoveRange(0, 2);

                ///Process Message Bundle here
                ProcessMessageBundle(MySession, MyPacket);
            }

            ///Set Bools to false, done processing
            Session_Ack = false;
            RDP_Report = false;
            Message_Bundle = false;
        }

        public static void ProcessMessageBundle(Session MySession, List<byte> MyPacket)
        {
            ///Need to consider how many messages could be in here, and message types
            ///FB/FA/40/F9
            ///
            while (MyPacket.Count() > 0)
            {
                ///Get our Message Type
                ushort MessageTypeOpcode = GrabOpcode(MyPacket);
                switch (MessageTypeOpcode)
                {
                    //General opcodes (0xFB, 0xF9's)
                    case MessageOpcodeTypes.ShortReliableMessage:
                    case MessageOpcodeTypes.LongReliableMessage:
                    case MessageOpcodeTypes.UnknownMessage:
                        ///Work on processing this opcode
                        ProcessOpcode.ProcessOpcodes(MySession, MessageTypeOpcode, MyPacket);
                        break;

                    //Client Actor update (0x4029's)
                    case UnreliableTypes.ClientActorUpdate:
                        ProcessUnreliable.ProcessUnreliables(MySession, MessageTypeOpcode, MyPacket);
                        break;


                    default:
                        //Shouldn't get here?
                        Console.WriteLine($"Received unknown Message: {MessageTypeOpcode}");

                        //Should we consume the whole message here if it is unknown so we can keep processing?
                        break;
                }  
            }
            MySession.RdpReport = true;
            MySession.ClientFirstConnect = true;

            //Reset ack timer
            MySession.ResetTimer();

            Logger.Info("Done processing messages in packet");
            ///Should we just initiate responses to clients through here for now?
            ///Ultimately we want to have a seperate thread with a server tick, 
            ///that may handle initiating sending messages at timed intervals, and initiating data collection such as C9's
        }

        private static void ProcessRdpReport(Session MySession, List<byte> MyPacket)
        {
            //Read client bundle here. Accept client bundle as is because packets could be lost or dropped, client bundle# should not be "tracked"
            //considerations could be to track it for possible drop/lost rates
            MySession.clientBundleNumber = (ushort)(MyPacket[1] << 8 | MyPacket[0]);
            ushort LastRecvBundleNumber = (ushort)(MyPacket[3] << 8 | MyPacket[2]);
            ushort LastRecvMessageNumber = (ushort)(MyPacket[5] << 8 | MyPacket[4]);

            //Check our list  of reliable messages to remove
            for (int i = 0; i < MySession.MyMessageList.Count(); i++)
            {
                //If our message stored is less then client ack, need to remove it!
                if(MySession.MyMessageList[i-i].ThisMessagenumber <= LastRecvMessageNumber)
                { MySession.MyMessageList.RemoveAt(0); }

                //Need to keep remaining messages, move on
                else { break; }
            }
            

            MyPacket.RemoveRange(0, 6);
            ///Only one that really matters, should tie into our packet resender stuff
            if (MySession.serverMessageNumber >= LastRecvMessageNumber)
            {

                ///This should be removing messages from resend mechanics.
                ///Update our last known message ack'd by client
                MySession.clientRecvMessageNumber = LastRecvMessageNumber;
            }

            ///Trigger Server Select with this?
            if (((MySession.clientEndpoint + 1) == (MySession.sessionIDBase & 0x0000FFFF)) && MySession.SessionAck && MySession.serverSelect == false)
            {
                ///Key point here for character select is (MySession.clientEndpoint + 1 == MySession.sessionIDBase)
                ///SessionIDBase is 1 more then clientEndPoint
                ///Assume it's Create Master session?
                Session NewMasterSession = new Session(MySession.clientEndpoint, MySession.MyIPEndPoint, MySession.AccountID);

                SessionManager.AddMasterSession(NewMasterSession);
            }

            ///Triggers creating master session
            else if ((MySession.clientEndpoint == (MySession.sessionIDBase & 0x0000FFFF)) && MySession.SessionAck && MySession.serverSelect == false)
            {
                MySession.serverSelect = true;
            }

            ///Triggers Character select
            else if (MySession.CharacterSelect && (MySession.clientEndpoint != MySession.sessionIDBase))
            {
                MySession.CharacterSelect = false;
                List<Character> MyCharacterList = new List<Character>();
                Logger.Info("Generating Character Select");
                MyCharacterList = SQLOperations.AccountCharacters(MySession);

                //Assign to our session
                MySession.CharacterData = MyCharacterList;

                ProcessOpcode.CreateCharacterList(MyCharacterList, MySession);
            }

            else if (MySession.Dumpstarted)
            {
                //Let client know more data is coming
                if (MySession.MyDumpData.Count() > 1156)
                {
                    List<byte> ThisChunk = MySession.MyDumpData.GetRange(0, 1156);
                    MySession.MyDumpData.RemoveRange(0, 1156);

                    ///Handles packing message into outgoing packet
                    RdpCommOut.PackMessage(MySession, ThisChunk, MessageOpcodeTypes.MultiShortReliableMessage);
                }

                //End of dump
                else
                {
                    List<byte> ThisChunk = MySession.MyDumpData.GetRange(0, MySession.MyDumpData.Count());
                    MySession.MyDumpData.Clear();
                    ///Handles packing message into outgoing packet
                    RdpCommOut.PackMessage(MySession, ThisChunk, MessageOpcodeTypes.ShortReliableMessage);
                    //turn dump off
                    MySession.Dumpstarted = false;
                }
            }

            else
            {
                Logger.Err($"Client received server message {LastRecvMessageNumber}, expected {MySession.serverMessageNumber}");
            }
        }

        private static void ProcessSessionAck(Session MySession, List<byte> MyPacket)
        {
            ///We are here
            uint SessionAck = (uint)(MyPacket[3] << 24 | MyPacket[2] << 16 | MyPacket[1] << 8 | MyPacket[0]);
            if (SessionAck == MySession.sessionIDBase)
            {
                MySession.ClientAck = true;
                MySession.Instance = true;
                /// Trigger Character select here
                Logger.Info("Beginning Character Select creation");
            }

            else
            {
                Console.WriteLine("Error");
                Logger.Err("Error occured with Session Ack Check...");
            }

            ///Remove these 4 bytes
            MyPacket.RemoveRange(0, 4);
        }

        ///This grabs the full Message Type. Checks for FF, if FF is present, then grab proceeding byte (FA or FB)
        private static ushort GrabOpcode(List<byte> MyPacket)
        {
            ushort Opcode = (ushort)MyPacket[0];
            ///If Message is > 255 bytes, Message type is prefixed with FF to indeificate this
            if (Opcode == 255)
            {
                Logger.Info("Received Long Message type (> 255 bytes)");
                Opcode = (ushort)(MyPacket[1] << 8 | MyPacket[0]);

                ///Remove 2 read bytes
                MyPacket.RemoveRange(0, 2);
                return Opcode;
            }

            ///Message type should be < 255 bytes
            else
            {
                Logger.Info("Received Normal Message type (< 255 bytes)");

                ///Remove read byte
                MyPacket.RemoveRange(0, 1);
                return Opcode;
            }
        }
    }

    class RdpCommOut
    {

        ///Message processing for outbound section
        public static void PackMessage(Session MySession, List<byte> myMessage, ushort MessageOpcodeType, ushort Opcode)
        {
            ///0xFB/FA type Message type
            if ((MessageOpcodeType == MessageOpcodeTypes.ShortReliableMessage) || (MessageOpcodeType == MessageOpcodeTypes.MultiShortReliableMessage))
            {
                ///Add our opcode
                myMessage.InsertRange(0, BitConverter.GetBytes(Opcode));

                ///Add Message #
                myMessage.InsertRange(0, BitConverter.GetBytes(MySession.serverMessageNumber));

                ///Pack Message here into MySession.SessionMessages
                ///Check message length first
                if ((myMessage.Count()) > 255)
                {
                    ///Add Message Length
                    myMessage.InsertRange(0, BitConverter.GetBytes((ushort)(myMessage.Count() - 2)));

                    ///Add out MessageType
                    myMessage.InsertRange(0, BitConverter.GetBytes((ushort)(0xFF00 ^ MessageOpcodeType)));
                }

                ///Message is < 255
                else
                {
                    ///Add Message Length
                    myMessage.Insert(0, (byte)(myMessage.Count() - 2));

                    ///Add out MessageType
                    myMessage.Insert(0, (byte)MessageOpcodeType);
                }

                //Add reliable Message to reliablemessage ack list
                MySession.AddMessage(MySession.serverMessageNumber, myMessage);

                //Increment server message #
                MySession.IncrementServerMessageNumber();
            }

            ///0xFC Message type
            else if (MessageOpcodeType == MessageOpcodeTypes.ShortUnreliableMessage)
            {
                ///Add our opcode
                myMessage.InsertRange(0, BitConverter.GetBytes(Opcode));

                ///Check message length first
                if ((myMessage.Count()) > 255)
                {
                    ///Add Message Length
                    myMessage.InsertRange(0, BitConverter.GetBytes((ushort)(myMessage.Count())));

                    ///Add out MessageType
                    myMessage.InsertRange(0, BitConverter.GetBytes(0xFF00 ^ MessageOpcodeType));
                }

                ///Message is < 255
                else
                {
                    ///Add Message Length
                    myMessage.Insert(0, (byte)(myMessage.Count()));

                    ///Add out MessageType
                    myMessage.Insert(0, (byte)MessageOpcodeType);
                }
            }
            ///Finally, add our message
            MySession.SessionMessages.AddRange(myMessage);

            ///We are packing to send a message, set MySession.RdpMessage to true
            MySession.RdpMessage = true;
        }

        public static void PackMessage(Session MySession, List<byte> myMessage, ushort MessageOpcodeType)
        {
            //Technically shouldn't need this if statement? But to be safe
            ///0xFB/FA/F9 Message type
            if ((MessageOpcodeType == MessageOpcodeTypes.ShortReliableMessage) || (MessageOpcodeType == MessageOpcodeTypes.MultiShortReliableMessage) || (MessageOpcodeType == MessageOpcodeTypes.UnknownMessage))
            {
                ///Add Message #
                myMessage.InsertRange(0, BitConverter.GetBytes(MySession.serverMessageNumber));

                ///Pack Message here into MySession.SessionMessages
                ///Check message length first
                if ((myMessage.Count()) > 255)
                {
                    ///Add Message Length
                    ///Swap endianness, then convert to bytes
                    myMessage.InsertRange(0, BitConverter.GetBytes((ushort)(myMessage.Count() - 2)));

                    ///Add our MessageType
                    myMessage.InsertRange(0, BitConverter.GetBytes((ushort)(0xFF00 ^ MessageOpcodeType)));
                }

                ///Message is < 255
                else
                {

                    ///Add Message Length (Remove the message #)
                    myMessage.Insert(0, (byte)(myMessage.Count() - 2));

                    ///Add out MessageType
                    myMessage.Insert(0, (byte)MessageOpcodeType);
                }

                //Add reliable Message to reliablemessage ack list
                MySession.AddMessage(MySession.serverMessageNumber, myMessage);

                ///Increment our internal message #
                MySession.IncrementServerMessageNumber();
            }

            ///Finally, add our message
            MySession.SessionMessages.AddRange(myMessage);

            ///We are packing to send a message, set MySession.RdpMessage to true
            MySession.RdpMessage = true;
        }

        ///Message processing for outbound section
        public static void PackMessage(Session MySession, ushort MessageOpcodeType, ushort Opcode)
        {
            List<byte> myMessage = new List<byte> { };

            ///Add our opcode
            myMessage.InsertRange(0, BitConverter.GetBytes(Opcode));

            //Add Message Length
            myMessage.Insert(0, 2);

            ///0xFB Message type
            if (MessageOpcodeType == MessageOpcodeTypes.ShortReliableMessage)
            {
                ///Add Message #
                myMessage.InsertRange(0, BitConverter.GetBytes(MySession.serverMessageNumber));

                ///Add out MessageType
                myMessage.Insert(0, (byte)MessageOpcodeType);

                //Add reliable Message to reliablemessage ack list
                MySession.AddMessage(MySession.serverMessageNumber, myMessage);

                ///Increment our internal message #
                MySession.IncrementServerMessageNumber();
            }

            ///0xFC Message type
            else if (MessageOpcodeType == MessageOpcodeTypes.ShortUnreliableMessage)
            {
                ///Add our MessageType
                myMessage.Insert(0, (byte)MessageOpcodeType);
            }

            MySession.SessionMessages.AddRange(myMessage);
            ///We are packing to send a message, set MySession.RdpMessage to true
            MySession.RdpMessage = true;
        }

        public static void PrepPacket(object source, ElapsedEventArgs e)
        {
            lock (SessionManager.SessionList)
            {
                foreach (Session MySession in SessionManager.SessionList)
                {
                    if ((MySession.RdpReport || MySession.RdpMessage) && MySession.ClientFirstConnect)
                    {
                        ///If creating outgoing packet, write this data to new list to minimize writes to session
                        List<byte> OutGoingMessage = new List<byte>();

                        ///Add our SessionMessages to this list
                        OutGoingMessage.AddRange(MySession.SessionMessages);

                        ///Clear client session Message List
                        MySession.SessionMessages.Clear();

                        Logger.Info("Packing header into packet");
                        ///Add RDPReport if applicable
                        AddRDPReport(MySession, OutGoingMessage);

                        ///Bundle needs to be incremented after every sent packet, seems like a good spot?
                        MySession.IncrementServerBundleNumber();

                        ///Add session ack here if it has not been done yet
                        ///Lets client know we acknowledge session
                        ///Making sure remoteMaster is 1 (client) makes sure we have them ack our session
                        if (!MySession.remoteMaster)
                        {
                            if (MySession.SessionAck == false)
                            {
                                ///To ack session, we just repeat session information as an ack
                                AddSession(MySession, OutGoingMessage);
                            }
                        }

                        ///Adds bundle type
                        AddBundleType(MySession, OutGoingMessage);

                        ///Get Packet Length
                        ushort PacketLength = (ushort)OutGoingMessage.Count();

                        ///Add Session Information
                        AddSession(MySession, OutGoingMessage);

                        ///Add the Session stuff here that has length built in with session stuff
                        AddSessionHeader(MySession, OutGoingMessage, PacketLength);

                        ///Done? Send to CommManagerOut
                        CommManagerOut.AddEndPoints(MySession, OutGoingMessage);
                    }

                    ///No packet needed to respond to client
                    else
                    {
                        Logger.Info("No Packet needed to respond to last message from client");
                    }
                }
            }
        }

        ///Identifies if full RDPReport is needed or just the current bundle #
        public static void AddRDPReport(Session MySession, List<byte> OutGoingMessage)
        {
            ///If RDP Report == True, Current bundle #, Last Bundle received # and Last message received #
            if (MySession.RdpReport == true)
            {
                Logger.Info("Full RDP Report");
                /// Add them to packet in "reverse order" stated above
                ///This swaps endianness of our Message received, then converts to bytes
                OutGoingMessage.InsertRange(0, BitConverter.GetBytes(MySession.clientMessageNumber));

                ///This swaps endianness of our Bundle received, then converts to bytes
                OutGoingMessage.InsertRange(0, BitConverter.GetBytes(MySession.clientBundleNumber));

                ///This swaps endianness of our Bundle, then converts to bytes
                OutGoingMessage.InsertRange(0, BitConverter.GetBytes(MySession.serverBundleNumber));
                
                //If ingame, include 4029 ack's
                if (MySession.InGame)
                {
                    OutGoingMessage.Insert(6, 0x40);
                    OutGoingMessage.InsertRange(7, BitConverter.GetBytes(MySession.Channel40Message));
                    OutGoingMessage.Insert(9, 0xF8);
                }
            }

            ///We should only add Servers current Bundle #, only when no messages received from client
            else
            {
                Logger.Info("Partial Rdp Report (Bundle #)");
                ///This swaps endianness of our Bundle, then converts to bytes
                OutGoingMessage.InsertRange(0, BitConverter.GetBytes(MySession.serverBundleNumber));
            }
        }

        ///Add our bundle type
        ///Consideration for in world or "certain packet" # is needed during conversion. For now something basic will work
        public static void AddBundleType(Session MySession, List<byte> OutGoingMessage)
        {
            ///Should this be a big switch statement?
            ///Using if else for now
            if (MySession.BundleTypeTransition)
            {
                ///If all 3 are true
                if (MySession.SessionAck == true && MySession.RdpMessage && MySession.RdpReport)
                {
                    Logger.Info("Adding Bundle Type 0x13");
                    OutGoingMessage.Insert(0, 0x13);
                }

                ///Message only packet, no RDP Report
                else if (MySession.SessionAck == true && MySession.RdpMessage && MySession.RdpReport == false)
                {
                    Logger.Info("Adding Bundle Type 0x00");
                    OutGoingMessage.Insert(0, 0x00);
                }

                ///RDP Report only
                else if (MySession.SessionAck == true && MySession.RdpMessage == false && MySession.RdpReport)
                {
                    Logger.Info("Adding Bundle Type 0x03");
                    OutGoingMessage.Insert(0, 0x03);
                }
            }

            else 
            {
                ///If Message and RDP report
                if (MySession.SessionAck == false && MySession.RdpMessage && MySession.RdpReport)
                {
                    Logger.Info("Adding Bundle Type 0x63");
                    OutGoingMessage.Insert(0, 0x63);
                    MySession.SessionAck = true;
                }

                ///Message only packet, no RDP Report
                else if (MySession.SessionAck == true && MySession.RdpMessage && MySession.RdpReport == false)
                {
                    Logger.Info("Adding Bundle Type 0x20");
                    OutGoingMessage.Insert(0, 0x20);
                }

                ///RDP Report only
                else if ((MySession.SessionAck == true && MySession.RdpMessage == false && MySession.RdpReport) || (MySession.SessionAck == true && MySession.RdpMessage && MySession.RdpReport))
                {
                    Logger.Info("Adding Bundle Type 0x23");
                    OutGoingMessage.Insert(0, 0x23);
                }
            }

            ///Reset our bools for next message to get proper Bundle Type
            MySession.Reset();

        }

        ///Add a session ack to send to client
        public static void AddSession(Session MySession, List<byte> OutGoingMessage)
        {
            Logger.Info("Adding Session Data");
            if (!MySession.remoteMaster)
            {
                ///Add first portion of session
                OutGoingMessage.InsertRange(0, BitConverter.GetBytes(MySession.sessionIDBase));
            }

            else
            {
                ///Ack session, first add 2nd portion of Session
                OutGoingMessage.InsertRange(0, Utility_Funcs.Technique(MySession.sessionIDUp));

                ///Add first portion of session
                OutGoingMessage.InsertRange(0, BitConverter.GetBytes(MySession.sessionIDBase));
            }

        }

        public static void AddSessionHeader(Session MySession, List<byte> OutGoingMessage, ushort PacketLength)
        {
            uint value = 0;
            Logger.Info("Adding Session Header");

            if (!MySession.Instance) //When server initiates instance with the client, it will use this
            {
                value |= 0x80000;
            }

            //This would be if we want to cancel the connection... Client handles this in most cases? For now.
            //if (MySession.CancelConnection) // 
            //{
            //    value += 0x10000;
            //}

            if (MySession.remoteMaster) // Purely a guess.... Something is 0x4000 in this and seems to correspond the initator of the session
            {
                value |= 0x04000;
            }

            else // Server is not master, seems to always have this when not in control
            {
                value |= 0x01000;
            }

            if(MySession.hasInstance) // Server always has instance ID, atleast untill we are in world awhile, then it can drop this and the 4 byte instance ID
            {
                value |= 0x02000;
            }

            //Add bundle length in
            value |= PacketLength;

            //Work some magic. Basic VLI
            List<byte> myList = new List<byte> { };

            int myVal = 0;
            int shift = 0;
            do
            {
                byte lower7bits = (byte)(value & 0x7f);
                value >>= 7;
                if (value > 0)
                {
                    myVal |= (lower7bits |= 128) << shift;
                    shift += 8;
                }
                else
                {
                    myVal |= lower7bits << shift;
                }
            } while (value > 0);

            myList.AddRange(BitConverter.GetBytes(myVal));
            int i = myList.Count - 1;
            int j = 0;
            while (myList[i] == 0) { j++; --i; }
            myList.RemoveRange(i + 1, j);

            ///Combine, switch endianness and place into Packet
            OutGoingMessage.InsertRange(0, myList);
        }
    }
}
