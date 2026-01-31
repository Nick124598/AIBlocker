using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UIStudent
{

    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        /// 
        
        public const int registeration_server_port = 5000;
        public static Socket _socket;
        [STAThread]
        static void Main()
        {
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            Application.ApplicationExit += OnApplicationExit;
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Application.Run(new Form1());
        }

        private static void OnApplicationExit(object? sender, EventArgs e)
        {
            Program._socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Program._socket.Connect(Form1._server_ip, registeration_server_port);
            Program._socket.Send(Encoding.UTF8.GetBytes("EXIT"));
            Program._socket.Close();
        }
    }
}