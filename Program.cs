using System;
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
            attract, username, password, login, mainMenu, goodbye, newMessages, writeMessage, bulletinBoard, newBulletin, readBulletin
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
            return Regex.Replace(Encoding.ASCII.GetString(data), "\\W", "");
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
                        SendBytes($"\fWelcome to 00bbs, {username}!\r\n\n");
                        SendBytes("--- MAIN MENU ---\r\n\n");
                        SendBytes("1) (N)ew messages\r\n2) (W)rite a message\r\n3) (B)ulletin board\r\n4) (G)oodbye\r\n\n> ");
                        data = new byte[1024];
                        length = s.Receive(data, 0, 1024, SocketFlags.None);
                        if(length == 0) done = true;
                        string command = GetString(data);
                        switch(command) {
                            case "1":
                            case "n":
                            case "N":
                                state = State.newMessages;
                                break;
                            case "2":
                            case "w":
                            case "W":
                                state = State.writeMessage;
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
