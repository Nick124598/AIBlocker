using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace UIStudent
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }


        void SetDns(string interfaceName, string dns)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"interface ip set dns name=\"{dns}\" static {interfaceName}",
                Verb = "runas",   // forces admin
                UseShellExecute = true,
                CreateNoWindow = true
            };

            Process.Start(psi);
        }

        private void button1_Click(object sender, EventArgs e)
        {
            const int registeration_server_port = 5000;
            object data = new
            {
                Username = textBox1.Text
            };
            string jsonData = JsonSerializer.Serialize(data);
            var ip = IPAddress.Parse(textBox2.Text);
            var remoteEP = new IPEndPoint(ip, registeration_server_port);

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(remoteEP);

            byte[] bytes = Encoding.UTF8.GetBytes(jsonData);
            socket.Send(bytes);

            bytes = new byte[1024];
            socket.Receive(bytes);
            string response = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            if (response == "OK")
            {
                MessageBox.Show("Registration successful! Good luck at the sadna!");
                SetDns("Wi-Fi", textBox2.Text);
                socket.Close();
            }
            else
            {
                MessageBox.Show("Registration failed: " + response);
                socket.Close();
                throw new Exception("Registration failed: " + response);
            }
            
        }
    }
}
