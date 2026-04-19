# Architecture Overview

This document provides a high-level architectural view of the TabularOps system.

## System Architecture

```mermaid
graph TD
    subgraph Core["Core Library (TabularOps.Core)"]
        CM["Connection Management<br/>(MSAL Token Cache,<br/>Per-Tenant Isolation)"]
        RE["Refresh Engines<br/>(TOM for SSAS/AAS,<br/>REST API for PBI/Fabric)"]
        TE["Tracing & Event<br/>Streaming"]
        DMV["DMV Components<br/>(Model Metadata)"]
    end

    subgraph UI["WPF Desktop Application"]
        VM["ViewModels"]
        Views["Views"]
    end

    subgraph Data["Data Persistence"]
        SQLite["SQLite<br/>(History & Cache)"]
    end

    CM --> RE
    CM --> TE
    CM --> DMV
    RE --> VM
    TE --> VM
    DMV --> VM
    VM --> Views
    RE -.-> SQLite
    TE -.-> SQLite

    style Core fill:#e1f5ff
    style UI fill:#fff3e0
    style Data fill:#f3e5f5
```

## Design Principles

- **Zero UI Dependencies in Core**: Core library maintains no references to WPF, ensuring reusability
- **Per-Tenant Isolation**: Connection management enforces tenant boundaries via MSAL token cache
- **Dual Refresh Paths**: 
  - TOM-based for SSAS/AAS models
  - REST API-based for Power BI and Fabric datasets
- **Event-Driven Tracing**: Streaming event model for real-time monitoring
- **Persistent History**: SQLite backing for refresh history and connection cache

## Key Components

### Core Library (TabularOps.Core)
The foundational layer containing:
- **Connection Management**: Handles multi-tenant scenarios with MSAL integration
- **Refresh Engines**: Abstracts TOM and REST API refresh operations
- **Tracing**: Event stream for monitoring and diagnostics
- **DMV Queries**: Dynamic Management View access for model metadata

### WPF Desktop Application
Consumer application built on the Core library:
- **ViewModels**: Business logic and state management
- **Views**: XAML-based user interface
- **Data Binding**: MVVM pattern implementation

### Data Persistence
- **SQLite Database**: Stores connection history, cache, and audit trails