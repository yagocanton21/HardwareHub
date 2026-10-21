# ⚡ Hardware HUB • Monitor & Central de Aplicativos

Aplicativo nativo para Windows construído em **C# .NET 9 (WPF)** com visual moderno baseado nas diretrizes do **Windows 11 Fluent Design**.

---

## 🎯 Recursos Implementados

### 1. 📊 Painel de Controle (Dashboard) & Sensores
* **Processador (CPU):** Nome completo do modelo, contagem de núcleos físicos e threads, porcentagem de uso em tempo real, frequência de clock (GHz) e temperatura (°C).
* **Placa Gráfica (GPU):** Nome da GPU dedicada/integrada, utilização em tempo real (%), temperatura da GPU (°C) e quantidade de memória VRAM utilizada vs total.
* **Memória RAM:** Capacidade total (GB), quantidade utilizada (GB), espaço livre e porcentagem de uso.
* **Armazenamento:** Detecção dinâmica de todos os discos locais (C:, D:, etc.) com capacidade total, espaço livre e barra de ocupação.
* **Identidade do Sistema:** Nome da máquina, versão do Windows e tempo de atividade contínuo (*Uptime*).

### 2. 📦 Central de Programas (Instalador com 1 Clique)
* Integração direta e silenciosa com o **`winget` (Windows Package Manager)**.
* **Catálogo inicial de aplicativos populares:**
  * **Navegadores:** Google Chrome, Mozilla Firefox, Brave Browser.
  * **Desenvolvimento & TI:** Visual Studio Code, Git for Windows, Node.js (LTS), Microsoft PowerToys.
  * **Jogos & Mídia:** Steam, Discord, Spotify, OBS Studio.
  * **Utilitários:** 7-Zip, VLC Media Player, Notepad++.
* **Detecção Automática:** Verifica no registro do Windows quais programas já estão instalados na sua máquina.
* **Filtros e Busca:** Busca rápida por nome e filtragem por pílulas de categoria.
* **Instalação Silenciosa:** Instala em segundo plano sem necessidade de avançar assistentes manuais.

### 3. 📋 Console & Logs
* Terminal integrado que exibe o output em tempo real das instalações e eventos do sistema.

---

## 🚀 Como Executar

### Opção 1: Via atalho rápido
Basta dar dois cliques no arquivo:
```cmd
run.bat
```

### Opção 2: Pelo terminal
```bash
cd src/HardwareHub
dotnet run
```

### Opção 3: Executável direto
O arquivo compilado está localizado em:
`src/HardwareHub/bin/Debug/net9.0-windows/HardwareHub.exe`

---

## 🛠️ Tecnologias Utilizadas
* **.NET 9 SDK** (C# nativo para Windows)
* **WPF + XAML** (Renderização acelerada por GPU DirectX)
* **LibreHardwareMonitorLib (v0.9.6)** (Leitura de sensores de hardware e temperaturas)
* **WPF-UI (v4.3.0)** (Controles Fluent Design do Windows 11)
* **System.Management (WMI)** (Consultas de especificações de hardware)
