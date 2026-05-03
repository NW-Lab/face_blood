//
//  FaceBloodApp.swift
//  FaceBlood
//

import SwiftUI

@main
struct FaceBloodApp: App {
    init() {
        // Lock to portrait + dark UI for the HUD aesthetic.
        UINavigationBar.appearance().tintColor = .white
    }

    var body: some Scene {
        WindowGroup {
            ContentView()
                .statusBarHidden(true)
                .persistentSystemOverlays(.hidden)
        }
    }
}
