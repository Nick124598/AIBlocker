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

        private void button1_Click(object sender, EventArgs e)
        {
            const int registeration_server_port = 5000;
            object data = new
            {
                Username = textBox1.Text
            };
            string jsonData = JsonSerializer.Serialize(data);
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(textBox2.Text, registeration_server_port);
            byte[] bytes = Encoding.UTF8.GetBytes(jsonData);
            socket.Send(bytes);
            socket.Close();
        }
    }
}
