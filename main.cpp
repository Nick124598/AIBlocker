#include <print>
#include <iostream>
#include "WSA.h"
#include <vector>
#include <thread>
#include <array>
#include <string>
#include <Ws2tcpip.h>


constexpr int MAX_CONNECTIONS_ALLOWED = 10;
constexpr int LISTENING_PORT = 53;
const char* listen_ip = "127.0.0.1"; //may need to be changed.
static std::vector<SOCKET> connections;

std::vector<std::string> blocked_domains = {
	"ai", "chatgpt", "openai", "gpt", "bard", "llm", "blackbox", "grok", "gemini", "github", "copilot",
	"deepmind", "anthropic", "claude", "huggingface", "stability", "midjourney", "dall-e"
};



SOCKET createSocket(const int port = LISTENING_PORT) {
	SOCKET sockfd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
	if (sockfd == INVALID_SOCKET) throw std::runtime_error("socket creation failed");
	struct sockaddr_in addr {};
	addr.sin_family = AF_INET;
	addr.sin_addr.s_addr = htonl(INADDR_ANY);
#ifdef WIN32
	addr.sin_port = htons(port);
#else
	addr.sin_port = port;
#endif
	if (bind(sockfd, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == SOCKET_ERROR) {
		const int err = WSAGetLastError();
		closesocket(sockfd);
		//std::cout << "failed";
#ifdef WIN32
		system("");
#else

#endif
		//throw std::runtime_error("bind failed: " + std::to_string(err));
	
	}
	//std::cout << "success";
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
	std::string domain = extract_domain_name(reinterpret_cast<const uint8_t*>(msg), bytesRecieved);
	std::cout << "Domain requested: " << domain << std::endl;
	for (const auto& blocked_domain : blocked_domains) {
		if (blocked_domain.find(domain) != std::string::npos) {
			std::cout << "Blocked domain access attempt: " << domain << std::endl;
			return;
		}
	}
	std::cout << "Allowed domain access: " << domain << std::endl;
}

void acceptUser(const SOCKET& listeningSocket) {
	sockaddr_in clientAddr{};
	int clientSize = sizeof(clientAddr);
	std::array<char, 4096> buffer{};

	if (connections.size() >= MAX_CONNECTIONS_ALLOWED) throw std::runtime_error("Too many connections"); 
	

	int bytesReceived = recvfrom(listeningSocket, buffer.data(), 4096, 0, (sockaddr*)&clientAddr, &clientSize);
	if (bytesReceived <= 0) return;
	std::string msg(buffer.data(), buffer.data() + bytesReceived);
	std::thread([m = std::move(msg)]() {
		parseMessage(m.data(), (int)m.size());
		}).detach();

	//buffer.fill('\0');
}

int main() {
	WSA wsa;
	connections.reserve(MAX_CONNECTIONS_ALLOWED + 1);
	SOCKET listeningSocket;
	try {
		listeningSocket = createSocket();
	} catch( const std::exception& e) {
		std::cout << e.what() << std::endl;
		return -1;
	}
	while (true) {
		try {
			acceptUser(listeningSocket);
		}
		catch (const std::exception& e) { std::cout << e.what() << std::endl; }
	}
	

	


	return 0;
}