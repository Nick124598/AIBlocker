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
        public static String _server_ip;

        void SetDns(string interfaceName, string dns)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-Command \"Set-DnsClientServerAddress -InterfaceAlias 'Wi-Fi' -ServerAddresses {dns},127.0.0.1\"",
                Verb = "runas",
                UseShellExecute = true
            };
            Process.Start(psi)?.WaitForExit();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            object data = new
            {
                Username = textBox1.Text
            };
            string jsonData = JsonSerializer.Serialize(data);
            _server_ip = textBox2.Text;
            var ip = IPAddress.Parse(_server_ip);
            var remoteEP = new IPEndPoint(ip, Program.registeration_server_port);
            Program._socket.Connect(remoteEP);

            byte[] bytes = Encoding.UTF8.GetBytes(jsonData);
            Program._socket.Send(bytes);

            bytes = new byte[1024];
            Program._socket.Receive(bytes);
            string response = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            if (response == "OK")
            {
                MessageBox.Show("Registration successful! Good luck at the sadna!");
                SetDns("Wi-Fi", textBox2.Text);
            }
            else
            {
                MessageBox.Show("Registration failed: " + response);
                Program._socket.Close();
                throw new Exception("Registration failed: " + response);
            }
        }

        private void Form1_Load(object sender, EventArgs e)
        {

        }
    }
}
