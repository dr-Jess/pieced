//
//  ContentView.swift
//  app
//
//  Created by Jesse Zong on 10/19/26.
//

import Combine
import SwiftUI
import UIKit

final class ConnectionModel: ObservableObject {
    let client = StreamClient()
    @Published var state: StreamClient.State = .disconnected

    init() {
        client.onStateChange = { [weak self] state in self?.state = state }
    }

    var isActive: Bool { state == .connected || state == .connecting }

    var statusText: String {
        switch state {
        case .disconnected: "Not connected"
        case .connecting: "Connecting…"
        case .connected: "Streaming to game"
        case .failed(let reason): "Failed: \(reason)"
        }
    }

    var statusColor: Color {
        switch state {
        case .connected: .green
        case .connecting: .orange
        case .failed: .red
        case .disconnected: .secondary
        }
    }
}

struct ContentView: View {
    @StateObject private var connection: ConnectionModel
    @StateObject private var motion: MotionManager
    @AppStorage("laptopHost") private var host = ""
    @AppStorage("laptopPort") private var port = 7777

    init() {
        let connection = ConnectionModel()
        _connection = StateObject(wrappedValue: connection)
        _motion = StateObject(wrappedValue: MotionManager(stream: connection.client))
    }

    var body: some View {
        VStack(spacing: 16) {
            Text("Pieced")
                .font(.title2.bold())

            connectionSection

            Divider()

            if let s = motion.latest {
                VStack(spacing: 4) {
                    Text(String(format: "accel  x: %+.2f  y: %+.2f  z: %+.2f g", s.ax, s.ay, s.az))
                    Text(String(format: "gyro   x: %+.2f  y: %+.2f  z: %+.2f", s.gx, s.gy, s.gz))
                    Text(String(format: "|accel| %.2f g", s.accelerationMagnitude))
                        .foregroundStyle(s.accelerationMagnitude > 1.8 ? .red : .primary)
                }
                .font(.system(.footnote, design: .monospaced))
            } else {
                Text("No motion data yet").foregroundStyle(.secondary)
            }

            Button(motion.isRunning ? "Stop Motion" : "Start Motion") {
                motion.isRunning ? motion.stop() : motion.start()
            }
            .buttonStyle(.borderedProminent)
            .disabled(!motion.isAvailable)

            if !motion.isAvailable {
                Text("Motion sensors unavailable (are you on the Simulator?)")
                    .foregroundStyle(.red)
            }

            List(motion.log.reversed()) { s in
                Text(String(format: "%.2f  %+.3f  %+.3f  %+.3f", s.t, s.ax, s.ay, s.az))
                    .font(.system(.caption, design: .monospaced))
            }
            .listStyle(.plain)
        }
        .padding()
        // Keep the screen on while streaming; the phone is in a hand, not being touched.
        .onChange(of: motion.isRunning) { _, running in
            UIApplication.shared.isIdleTimerDisabled = running
        }
    }

    private var connectionSection: some View {
        VStack(spacing: 8) {
            HStack {
                TextField("Laptop IP (shown in the game)", text: $host)
                    .keyboardType(.numbersAndPunctuation)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                TextField("Port", value: $port, format: .number.grouping(.never))
                    .keyboardType(.numberPad)
                    .frame(width: 70)
            }
            .textFieldStyle(.roundedBorder)
            .disabled(connection.isActive)

            Button(connection.isActive ? "Disconnect" : "Connect") {
                if connection.isActive {
                    connection.client.disconnect()
                } else {
                    connection.client.connect(host: host.trimmingCharacters(in: .whitespaces),
                                              port: UInt16(clamping: port),
                                              deviceName: UIDevice.current.name)
                    if !motion.isRunning { motion.start() }
                }
            }
            .buttonStyle(.bordered)

            Text(connection.statusText)
                .font(.footnote)
                .foregroundStyle(connection.statusColor)
        }
    }
}

#Preview { ContentView() }
