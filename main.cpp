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



SOCKET createSocket(const int port = 5300) {
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
	if (bind(sockfd, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == SOCKET_ERROR) { closesocket(sockfd); throw std::runtime_error("bind failed"); }
	return sockfd;
}

std::string extract_domain_name(const uint8_t* dns, size_t length) {
	std::string domain;
	size_t offset = 12;  // DNS header

	while (offset < length) {
		uint8_t label_len = dns[offset++];
		if (label_len == 0)
			break;

		if (!domain.empty())
			domain += '.';

		if (offset + label_len > length)
			return "";  // malformed packet

		domain.append(reinterpret_cast<const char*>(dns + offset), label_len);
		offset += label_len;
	}

	return domain;
}

void parseMessage(const char* msg, int bytesRecieved) {
	std::cout << "Received message:" << extract_domain_name((const uint8_t*)msg, bytesRecieved) << std::endl;
}

void acceptUser(const SOCKET& listeningSocket) {
	sockaddr_in clientAddr{};
	int clientSize = sizeof(clientAddr);
	if (connections.size() >= MAX_CONNECTIONS_ALLOWED) throw std::runtime_error("Too many connections"); 
	
	//std::array<char, 4096> buffer;
	char buffer[4096] = { 0 };

	int bytesRecieved = recvfrom(listeningSocket, buffer, 4096, 0, (sockaddr*)&clientAddr, &clientSize);

	//connections.push_back();
	std::thread t(parseMessage, buffer, bytesRecieved);
	t.detach();
	//buffer.fill('\0');
}

int main() {
	WSA wsa;
	connections.reserve(MAX_CONNECTIONS_ALLOWED + 1);
	SOCKET listeningSocket = createSocket();
	while (true) {
		try {
			acceptUser(listeningSocket);
		}
		catch (const std::exception& e) { std::cout << e.what() << std::endl; }
	}
	

	


	return 0;
}