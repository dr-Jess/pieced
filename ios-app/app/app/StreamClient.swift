//
//  StreamClient.swift
//  app
//
//  TCP client that streams newline-delimited JSON to the game on the laptop.
//  Protocol: tools/stream/README.md
//

import Foundation
import Network

/// All work happens on a private serial queue, so `send` is safe to call from the CoreMotion queue.
nonisolated final class StreamClient: @unchecked Sendable {
    enum State: Equatable, Sendable {
        case disconnected
        case connecting
        case connected
        case failed(String)
    }

    static let protocolVersion = 1

    /// Called on the main actor whenever the connection state changes.
    var onStateChange: (@MainActor @Sendable (State) -> Void)?

    private let queue = DispatchQueue(label: "pieced.stream-client")
    private let encoder = JSONEncoder()
    private var connection: NWConnection?
    private var isReady = false

    func connect(host: String, port: UInt16, deviceName: String) {
        queue.async { [self] in
            teardown()
            guard let nwPort = NWEndpoint.Port(rawValue: port), !host.isEmpty else {
                publish(.failed("Invalid host or port"))
                return
            }
            let tcp = NWProtocolTCP.Options()
            tcp.noDelay = true          // send each sample immediately instead of batching (Nagle)
            tcp.connectionTimeout = 5
            let connection = NWConnection(host: NWEndpoint.Host(host), port: nwPort,
                                          using: NWParameters(tls: nil, tcp: tcp))
            self.connection = connection

            connection.stateUpdateHandler = { [weak self, weak connection] state in
                guard let self, let connection, connection === self.connection else { return }
                switch state {
                case .ready:
                    self.isReady = true
                    self.write(Hello(name: deviceName))
                    self.receive(on: connection)
                    self.publish(.connected)
                case .waiting(let error):
                    // e.g. connection refused: the game isn't running or the IP is wrong.
                    self.publish(.failed(error.localizedDescription))
                    self.teardown()
                case .failed(let error):
                    self.publish(.failed(error.localizedDescription))
                    self.teardown()
                case .preparing:
                    self.publish(.connecting)
                default:
                    break
                }
            }
            publish(.connecting)
            connection.start(queue: queue)
        }
    }

    func disconnect() {
        queue.async { [self] in
            teardown()
            publish(.disconnected)
        }
    }

    /// Drops the sample if not connected.
    func send(_ sample: MotionSample) {
        queue.async { [self] in write(sample) }
    }

    // MARK: - Private (queue only)

    private func write<T: Encodable>(_ message: T) {
        guard isReady, let connection, var data = try? encoder.encode(message) else { return }
        data.append(0x0A) // newline terminates each message
        connection.send(content: data, completion: .contentProcessed { _ in })
    }

    /// The game doesn't send anything yet; reading lets us notice when it closes the socket.
    private func receive(on connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 4096) { [weak self] _, _, isComplete, error in
            guard let self, connection === self.connection else { return }
            if isComplete || error != nil {
                self.teardown()
                self.publish(.failed("Game closed the connection"))
            } else {
                self.receive(on: connection)
            }
        }
    }

    private func teardown() {
        isReady = false
        connection?.stateUpdateHandler = nil
        connection?.cancel()
        connection = nil
    }

    private func publish(_ state: State) {
        guard let onStateChange else { return }
        Task { @MainActor in onStateChange(state) }
    }
}

nonisolated private struct Hello: Encodable {
    var type = "hello"
    var role = "phone"
    let name: String
    var `protocol` = StreamClient.protocolVersion
}
