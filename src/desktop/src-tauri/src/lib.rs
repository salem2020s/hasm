// The desktop shell only hosts the shared React UI. All data access goes through the Backend API over HTTPS.
#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .run(tauri::generate_context!())
        .expect("error while running HASM desktop application");
}
