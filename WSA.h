#pragma once
#include <WinSock2.h>
#include <stdexcept>

#pragma comment(lib, "Ws2_32.lib")

class WSA
{
private:
	WSADATA wsaData;
public:
	WSA() {
		if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0)
			throw std::runtime_error("WSAStartup failed");
	};
	~WSA() {
		WSACleanup();
	};
};