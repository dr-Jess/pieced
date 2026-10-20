//
//  ContentView.swift
//  app
//
//  Created by Jesse Zong on 10/19/26.
//

import SwiftUI

struct ContentView: View {
    @StateObject private var motion = MotionManager()

    var body: some View {
        VStack(spacing: 16) {
            Text("Accelerometer")
                .font(.title2.bold())

            if let s = motion.latest {
                Text(String(format: "x: %+.3f   y: %+.3f   z: %+.3f", s.x, s.y, s.z))
                    .font(.system(.body, design: .monospaced))
            } else {
                Text("No data yet").foregroundStyle(.secondary)
            }

            Button(motion.isRunning ? "Stop" : "Start") {
                motion.isRunning ? motion.stop() : motion.start()
            }
            .buttonStyle(.borderedProminent)
            .disabled(!motion.isAvailable)

            if !motion.isAvailable {
                Text("Accelerometer unavailable (are you on the Simulator?)")
                    .foregroundStyle(.red)
            }

            List(motion.log.reversed()) { s in
                Text(String(format: "%.2f  %+.3f  %+.3f  %+.3f", s.timestamp, s.x, s.y, s.z))
                    .font(.system(.caption, design: .monospaced))
            }
            .listStyle(.plain)
        }
        .padding()
    }
}

#Preview { ContentView() }
