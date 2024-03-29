﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace bbs
{
    class Program
    {
        public static List<ConnectionJob> clients = new List<ConnectionJob>();
        public static SpinLock spinLock = new SpinLock();
        static void Main(string[] args)
        {
            Console.WriteLine("00bbs v1.0");

            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            IPEndPoint ep = new IPEndPoint(0, 55000);
            s.Bind(ep);
            s.Listen(4);

            while(true)
                new ConnectionJob(s.Accept());
        }
    }

    class ConnectionJob
    {
        private Socket s;
        private bool lockTaken;
        private byte[] data = new byte[1024];

        private enum State {
            attract, username, password, login, mainMenu, goodbye, newMessages, writeMessageRecipient, writeMessage, bulletinBoard, newBulletin, readBulletin
        };

        public ConnectionJob(Socket s)
        {
            this.s = s;
            Thread thread = new Thread(this.Job);
            thread.Start();
        }

        private void SendBytes(string msg) {
            s.Send(Encoding.ASCII.GetBytes(msg));
        }

        private string GetString(byte[] data) {
            return Regex.Replace(Encoding.ASCII.GetString(data), "[^ -~]+", "");
        }

        public void Job() {
            lockTaken = false;
            Program.spinLock.Enter(ref lockTaken);
            Program.clients.Add(this);
            Program.spinLock.Exit();
            Console.WriteLine($"Client connected  - {s.LocalEndPoint.ToString()}");

            SQLiteConnection conn = new SQLiteConnection(@"URI=file:/home/badino/bbs/bbs.db");
            conn.Open();
            var cmd = new SQLiteCommand(conn);

            State state = State.attract;

            string username = "", password = "";
            int uid = 0;
            int length;

            int bulletinIndex = 0;
            List<string[]> bulletin = new List<string[]>();
            List<DateTime> bulletinDates = new List<DateTime>();

            int messageIndex = 0;
            List<string[]> messages = new List<string[]>();
            List<DateTime> messagesDates = new List<DateTime>();
            List<int> messageIds = new List<int>();

            int rid = -1;

            bool done = false;
            while(!done) {
                data = new byte[1024];
                switch(state) {
                    case State.attract:
                        SendBytes("00bbs v0.1\r\n\n");
                        SendBytes(" ___  ___  _    _         \r\n");
                        SendBytes("/   \\/   \\| |  | |    ___ \r\n");
                        SendBytes("| | || | || |_ | |_  / _/ \r\n");
                        SendBytes("| | || | ||   \\|   \\ \\ \\  \r\n");
                        SendBytes("| | || | ||  |||  || _\\ \\ \r\n");
                        SendBytes("\\___/\\___/|___/|___//___/ \r\n\n");
                        length = s.Receive(data, 0, 1024, SocketFlags.None);
                        if(length == 0) done = true;
                        state = State.username;
                        break;
                    case State.username:
                        SendBytes("--- LOGIN ---\r\n\n");
                        SendBytes("Username: ");
                        length = s.Receive(data, 0, 1024, SocketFlags.None);
                        if(length == 0) done = true;
                        username = GetString(data);
                        Console.WriteLine($"Login attempt @ {username}");
                        state = State.password;
                        break;
                    case State.password:
                        SendBytes("Password: ");
                        length = s.Receive(data, 0, 1024, SocketFlags.None);
                        if(length == 0) done = true;
                        password = GetString(data);
                        state = State.login;
                        break;
                    case State.login:
                        cmd = new SQLiteCommand(conn);
                        cmd.CommandText = "SELECT COUNT(*), id FROM users WHERE username LIKE @username AND password LIKE @password";
                        cmd.Parameters.AddWithValue("@username", username);
                        cmd.Parameters.AddWithValue("@password", password);
                        cmd.Prepare();
                        var reader = cmd.ExecuteReader();
                        while(reader.Read()) {
                            if(reader.GetInt32(0) != 1) {
                                Console.WriteLine($"Login failed @ {username} - username and password combination not found in database");
                                SendBytes("\nBAD USERNAME OR PASSWORD\r\n\n");
                                state = State.username;
                            } else {
                                Console.WriteLine($"Login succeeded @ {username}");
                                uid = reader.GetInt32(1);
                                state = State.mainMenu;
                            }
                        }
                        reader.Close();
                        break;
                    case State.mainMenu:
                        // load new messages
                        cmd = new SQLiteCommand(conn);
                        cmd.CommandText = "SELECT id, message, fromuser, (SELECT username FROM users WHERE users.id = messages.fromuser) AS fromusername, sent FROM messages WHERE touser = @touser";
                        cmd.Parameters.AddWithValue("@touser", uid);
                        cmd.Prepare();
                        reader = cmd.ExecuteReader();
                        messages = new List<string[]>();
                        messageIds = new List<int>();
                        messagesDates = new List<DateTime>();
                        if(reader.HasRows)
                            while(reader.Read()) {
                                messages.Add(new string[]{reader.GetString(3), reader.GetString(1)});
                                messageIds.Add(reader.GetInt32(0));
                                messagesDates.Add(reader.GetDateTime(4));
                            }
                        reader.Close();
                        messageIndex = messages.Count - 1;
                        // render the main menu
                        SendBytes($"\fWelcome to 00bbs, {username}!\r\n\n");
                        SendBytes("--- MAIN MENU ---\r\n\n");
                        SendBytes($"1) (R)ead messages ({messages.Count})\r\n2) (W)rite a message\r\n3) (B)ulletin board\r\n4) (G)oodbye\r\n\n> ");
                        data = new byte[1024];
                        length = s.Receive(data, 0, 1024, SocketFlags.None);
                        if(length == 0) done = true;
                        string command = GetString(data);
                        switch(command) {
                            case "1":
                            case "r":
                            case "R":
                                state = State.newMessages;
                                break;
                            case "2":
                            case "w":
                            case "W":
                                state = State.writeMessageRecipient;
                                break;
                            case "3":
                            case "b":
                            case "B":
                                state = State.bulletinBoard;
                                break;
                            case "4":
                            case "g":
                            case "G":
                                state = State.goodbye;
                                break;
                        }
                        break;
                    case State.newMessages:
                        if(messages.Count == 0) {
                            state = State.mainMenu;
                            break;
                        }
                        SendBytes($"\f\n\n--- READ MESSAGES - {messageIndex + 1} OUT OF {messages.Count} ---\r\n\n");
                        SendBytes($"FROM: {messages[messageIndex][0]}\r\n");
                        SendBytes($"POSTED @ {messagesDates[messageIndex]}\r\n\n");
                        SendBytes($"MESSAGE:\r\n{messages[messageIndex][1]}\r\n\n");
                        SendBytes("1) (N)ext\r\n2) (P)revious\r\n3) (D)elete\r\n4) (M)enu\r\n\n> ");
                        data = new byte[1024];
                        length = s.Receive(data, 0, 1024, SocketFlags.None);
                        if(length == 0) done = true;
                        command = GetString(data);
                        switch(command) {
                            case "1":
                            case "n":
                            case "N":
                                messageIndex++;
                                if(messageIndex >= messages.Count) messageIndex = messages.Count - 1;
                                break;
                            case "2":
                            case "p":
                            case "P":
                                messageIndex--;
                                if(messageIndex < 0) messageIndex = 0;
                                break;
                            case "3":
                            case "d":
                            case "D":
                                cmd = new SQLiteCommand(conn);
                                cmd.CommandText = "DELETE FROM messages WHERE id = @id";
                                cmd.Parameters.AddWithValue("@id", messageIds[messageIndex]);
                                cmd.Prepare();
                                cmd.ExecuteNonQuery();
                                messages.RemoveAt(messageIndex);
                                messageIds.RemoveAt(messageIndex);
                                messagesDates.RemoveAt(messageIndex);
                                if(messageIndex >= messages.Count) messageIndex--;
                                if(messageIndex == 0) if(messages.Count == 0) state = State.mainMenu;
                                break;
                            case "4":
                            case "m":
                            case "M":
                                state = State.mainMenu;
                                break;
                        }
                        break;
                    case State.writeMessageRecipient:
                        SendBytes("\f\n\n--- NEW MESSAGE ---\r\n\n");
                        SendBytes($"TO (EMPTY TO CANCEL {(uid == 1 ? ", GUESTS CAN ONLY MAIL THE ADMIN" : "")})> ");
                        data = new byte[1024];
                        length = s.Receive(data, 0, 1024, SocketFlags.None);
                        if(length == 0) done = true;
                        string recipient = GetString(data);
                        if(recipient.Equals("")) {
                            state = State.mainMenu;
                            break;
                        }
                        if(uid == 1) {
                            SendBytes("USER IS GUEST, IGNORING AND SENDING TO ADMIN\r\n");
                            recipient = "admin";
                        }
                        cmd = new SQLiteCommand(conn);
                        cmd.CommandText ="SELECT id FROM users WHERE username LIKE @username";
                        cmd.Parameters.AddWithValue("@username", recipient);
                        cmd.Prepare();
                        reader = cmd.ExecuteReader();
                        if(reader.HasRows) {
                            while(reader.Read())
                                rid = reader.GetInt32(0);
                            reader.Close();
                        } else {
                            SendBytes("\r\n\nINVALID USERNAME\r\nPRESS ENTER TO RETRY");
                            data = new byte[1024];
                            length = s.Receive(data, 0, 1024, SocketFlags.None);
                            if(length == 0) done = true;
                        }
                        state = State.writeMessage;
                        break;
                    case State.writeMessage:
                        SendBytes("\r\nMESSAGE (EMPTY TO CANCEL)>\r\n");
                        data = new byte[1024];
                        length = s.Receive(data, 0, 1024, SocketFlags.None);
                        if(length == 0) done = true;
                        string message = GetString(data);
                        if(message.Equals("")) {
                            state = State.mainMenu;
                            break;
                        }
                        cmd = new SQLiteCommand(conn);
                        cmd.CommandText = "INSERT INTO messages (message, fromuser, touser, sent) VALUES (@message, @fromuser, @touser, @sent)";
                        cmd.Parameters.AddWithValue("@message", message);
                        cmd.Parameters.AddWithValue("@fromuser", uid);
                        cmd.Parameters.AddWithValue("@touser", rid);
                        cmd.Parameters.AddWithValue("@sent", DateTime.Now);
                        cmd.Prepare();
                        cmd.ExecuteNonQuery();
                        state = State.mainMenu;
                        break;
                    case State.bulletinBoard:
                        SendBytes("\f\n\n--- BULLETIN BOARD ---\r\n\n");
                        SendBytes("1) (R)ead\r\n2) (P)ost\r\n3) (M)ain menu\r\n\n> ");
                        data = new byte[1024];
                        length = s.Receive(data, 0, 1024, SocketFlags.None);
                        if(length == 0) done = true;
                        command = GetString(data);
                        switch(command) {
                            case "1":
                            case "r":
                            case "R":
                                state = State.readBulletin;
                                break;
                            case "2":
                            case "p":
                            case "P":
                                state = State.newBulletin;
                                break;
                            case "3":
                            case "m":
                            case "M":
                                state = State.mainMenu;
                                break;
                        }
                        cmd = new SQLiteCommand(conn);
                        cmd.CommandText = "SELECT message, posted, username FROM bulletin, users WHERE bulletin.uid = users.id";
                        var rdr = cmd.ExecuteReader();
                        bulletinIndex = 0;
                        bulletin = new List<string[]>();
                        bulletinDates = new List<DateTime>();
                        while (rdr.Read()) {
                            bulletin.Add(new string[]{rdr.GetString(0), rdr.GetString(2)});
                            bulletinDates.Add(rdr.GetDateTime(1));
                        }
                        bulletinIndex = bulletin.Count - 1;
                        rdr.Close();
                        break;
                    case State.readBulletin:
                        SendBytes("\f\n\n--- BULLETIN BOARD - MESSAGES ---\r\n\n");
                        SendBytes($"FROM: {bulletin[bulletinIndex][1]}\r\n");
                        SendBytes($"POSTED @ {bulletinDates[bulletinIndex]}\r\n\n");
                        SendBytes($"MESSAGE:\r\n{bulletin[bulletinIndex][0]}\r\n\n");
                        SendBytes("1) (N)ext\r\n2) (P)revious\r\n3) (M)enu\r\n\n> ");
                        data = new byte[1024];
                        length = s.Receive(data, 0, 1024, SocketFlags.None);
                        if(length == 0) done = true;
                        command = GetString(data);
                        switch(command) {
                            case "1":
                            case "n":
                            case "N":
                                bulletinIndex++;
                                if(bulletinIndex >= bulletin.Count) bulletinIndex = bulletin.Count - 1;
                                break;
                            case "2":
                            case "p":
                            case "P":
                                bulletinIndex--;
                                if(bulletinIndex < 0) bulletinIndex = 0;
                                break;
                            case "3":
                            case "m":
                            case "M":
                                state = State.bulletinBoard;
                                break;
                        }
                        break;
                    case State.newBulletin:
                        if(uid == 1) {
                            SendBytes("\f\n\nGUESTS CANNOT PIN MESSAGES TO THE BULLETIN BOARD\r\n\nPRESS ENTER TO RETURN");
                            data = new byte[1024];
                            length = s.Receive(data, 0, 1024, SocketFlags.None);
                            if(length == 0) done = true;
                            state = State.bulletinBoard;
                        } else {
                            SendBytes("\f\n\n--- BULLETIN BOARD - NEW MESSAGE ---\r\n\n");
                            SendBytes("Write your message and confirm by pressing ENTER\r\nTo cancel, write nothing and press ENTER\r\n\nMESSAGE:\r\n\n");
                            data = new byte[1024];
                            length = s.Receive(data, 0, 1024, SocketFlags.None);
                            if(length == 0) done = true;
                            message = GetString(data);
                            if(message.Length > 0) {
                                Console.WriteLine($"New post from {username}");
                                cmd = new SQLiteCommand(conn);
                                cmd.CommandText = "INSERT INTO bulletin (message, uid, posted) VALUES (@message, @uid, @posted)";
                                cmd.Parameters.AddWithValue("@message", message);
                                cmd.Parameters.AddWithValue("@uid", uid);
                                cmd.Parameters.AddWithValue("@posted", DateTime.Now);
                                cmd.Prepare();
                                cmd.ExecuteNonQuery();
                            }
                            state = State.bulletinBoard;
                        }
                        break;
                    case State.goodbye:
                        SendBytes("");
                        done = true;
                        break;
                    default:
                        length = s.Receive(data, 0, 1024, SocketFlags.None);
                        if(length == 0) done = true;
                        break;
                }
            }

            Console.WriteLine($"Client disconnected - {s.LocalEndPoint.ToString()}");
            s.Close();
            lockTaken = false;
            Program.spinLock.Enter(ref lockTaken);
            Program.clients.Remove(this);
            Program.spinLock.Exit();
        }
    }
}
