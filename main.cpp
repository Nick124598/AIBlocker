#include <print>
#include <iostream>
#include "WSA.h"
#include <vector>
#include <thread>
#include <array>


constexpr int MAX_CONNECTIONS_ALLOWED = 10;
constexpr int LISTENING_PORT = 53;
const char* listen_ip = "127.0.0.1"; //may need to be changed.
static std::vector<SOCKET> connections;



SOCKET createSocket(const int port = 53) {
	SOCKET sockfd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
	if (sockfd == INVALID_SOCKET) throw std::runtime_error("socket creation failed");
	struct sockaddr_in addr {};
	addr.sin_family = AF_INET;
	addr.sin_addr.s_addr = htonl(INADDR_ANY);
#ifdef WIN32
	addr.sin_port = htons(port);
#else
	server_addr.sin_port = port;
#endif
	if (bind(sockfd, reinterpret_cast<sockaddr*> &addr, sizeof(addr)) == SOCKET_ERROR) { closesocket(sockfd); throw std::runtime_error("bind failed"); }
	return sockfd;
}

void parseMessage(const char* msg) {
	std::cout << "Received message:" << msg << std::endl;
}

void acceptUser(const SOCKET& listeningSocket) {
	sockaddr_in clientAddr{};
	int clientSize = sizeof(clientAddr);
	if (connections.size() >= MAX_CONNECTIONS_ALLOWED) throw std::runtime_error("Too many connections"); 
	
	//std::array<char, 4096> buffer;
	char buffer[4096] = { 0 };

	int bytesRecieved = recvfrom(listeningSocket, buffer, 4096, 0, (sockaddr*)&clientAddr, &clientSize);

	//connections.push_back();
	std::thread t(parseMessage, &buffer[0]);
	t.detach();
	//buffer.fill('\0');
}

int main() {
	WSA wsa;
	connections.reserve(MAX_CONNECTIONS_ALLOWED + 1);
	try {
		SOCKET listeningSocket = createSocket();
		acceptUser(listeningSocket);
	}
	catch (const std::exception& e) { std::cout << e.what() << std::endl; }
	

	


	return 0;
}