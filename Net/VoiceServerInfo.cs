namespace OpenCord;

// The voice-gateway handshake, handed over by UserClient when VOICE_SERVER_UPDATE lands.
//
// ChannelId is here rather than being read off the first proposals message because Discord's DAVE
// (end-to-end encryption) derives its MLS group id from the 64-bit channel snowflake, and every
// member has to create their local group *before* proposals start arriving.
//
// This is a protocol type, so it sits in Net/ next to the client that produces it — the predecessor
// declared it in the voice UI file, which is why the network layer would not compile on its own.
record VoiceServerInfo(string Endpoint, string Token, ulong ServerId, string SessionId, ulong UserId,
                       ulong ChannelId);
