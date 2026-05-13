# Cohesion

Cohesion is 

- [Cohesion](#cohesion)
- [Sdk](#sdk)
  - [Libraries](#libraries)
  - [Services/Resources](#servicesresources)
  - [Tooling](#tooling)
  - [Extensions](#extensions)
- [Repository Structure](#repository-structure)

# Sdk
![SDK](https://github.com/assimalign/cohesion/actions/workflows/framework.yml/badge.svg?branch=main)

Cohesion is a mono repository 

## Libraries
![amqp](https://github.com/assimalign/cohesion/actions/workflows/library-amqp.yml/badge.svg?branch=main)
![application model](https://github.com/assimalign/cohesion/actions/workflows/library-application-model.yml/badge.svg?branch=main)
![configuration](https://github.com/assimalign/cohesion/actions/workflows/library-configuration.yml/badge.svg?branch=main)
![content](https://github.com/assimalign/cohesion/actions/workflows/library-content.yml/badge.svg?branch=main)
![core](https://github.com/assimalign/cohesion/actions/workflows/library-core.yml/badge.svg?branch=main)
![dns](https://github.com/assimalign/cohesion/actions/workflows/library-dns.yml/badge.svg?branch=main)
![filesystem](https://github.com/assimalign/cohesion/actions/workflows/library-filesystem.yml/badge.svg?branch=main)
![hosting](https://github.com/assimalign/cohesion/actions/workflows/library-hosting.yml/badge.svg?branch=main)
![http](https://github.com/assimalign/cohesion/actions/workflows/library-http.yml/badge.svg?branch=main)
![identity model](https://github.com/assimalign/cohesion/actions/workflows/library-identity-model.yml/badge.svg?branch=main)
![logging](https://github.com/assimalign/cohesion/actions/workflows/library-logging.yml/badge.svg?branch=main)
![openapi](https://github.com/assimalign/cohesion/actions/workflows/library-openapi.yml/badge.svg?branch=main)
![opentelemetry](https://github.com/assimalign/cohesion/actions/workflows/library-opentelemetry.yml/badge.svg?branch=main)
![resilience](https://github.com/assimalign/cohesion/actions/workflows/library-resilience.yml/badge.svg?branch=main)



## Services/Resources
![api manager](https://github.com/assimalign/cohesion/actions/workflows/resource-api-manager.yml/badge.svg?branch=main)
![Configuration Store](https://github.com/assimalign/cohesion/actions/workflows/resource-configuration-store.yml/badge.svg?branch=main)
![database](https://github.com/assimalign/cohesion/actions/workflows/resource-database.yml/badge.svg?branch=main)
![event hub](https://github.com/assimalign/cohesion/actions/workflows/resource-event-hub.yml/badge.svg?branch=main)
![iot hub](https://github.com/assimalign/cohesion/actions/workflows/resource-iot-hub.yml/badge.svg?branch=main)
![load balancer](https://github.com/assimalign/cohesion/actions/workflows/resource-load-balancer.yml/badge.svg?branch=main)
![log space](https://github.com/assimalign/cohesion/actions/workflows/resource-log-space.yml/badge.svg?branch=main)
![media hub](https://github.com/assimalign/cohesion/actions/workflows/resource-media-hub.yml/badge.svg?branch=main)
![message hub](https://github.com/assimalign/cohesion/actions/workflows/resource-message-hub.yml/badge.svg?branch=main)
![nat gateway](https://github.com/assimalign/cohesion/actions/workflows/resource-nat-gateway.yml/badge.svg?branch=main)
![notification hub](https://github.com/assimalign/cohesion/actions/workflows/resource-notification-hub.yml/badge.svg?branch=main)
![secret store](https://github.com/assimalign/cohesion/actions/workflows/resource-secret-store.yml/badge.svg?branch=main)
![web](https://github.com/assimalign/cohesion/actions/workflows/resource-web.yml/badge.svg?branch=main)

The service section of this repository is broken into a two layer approach folder structure `Layer 1 [Service/Resource] -> Layer 2 [Library]`. This approach

## Tooling

## Extensions

# Repository Structure

Cohesion is a mono repository that contains all the source code, extensions, and tooling in one. This allows for easier development. When working with cohesion it's best to scope development to specific areas of the repository 

The following l

| Folder         | Usage                                                                 |
| -------------- | --------------------------------------------------------------------- |
| `./.build`     | This contains all the scripts and process for packaging the SDK which |
| `./.docs`      |                                                                       |
| `./.samples`   |                                                                       |
| `./libraries`  | All the source code for cohesion lives in the following folder.       |
| `./tooling`    |                                                                       |
| `./extensions` |                                                                       |
| `./sdk`        | This contains source code for MSBuild SDK Style Project.              |




```mermaid
graph TD
    subgraph W1["Wave 1: Anchors"]
        Core["Core L01.01.06"]
        Security["Security L01.01.18"]
    end

    subgraph W2["Wave 2: Core Infrastructure"]
        DI["DependencyInjection L01.01.07"]
        Config["Configuration L01.01.04"]
        Logging["Logging L01.01.13"]
        FS["FileSystem L01.01.09"]
        Net["Net L01.01.14"]
        Cache["Cache L01.01.03"]
        Resilience["Resilience L01.01.17"]
    end

    subgraph W3["Wave 3: Protocol & Format"]
        Http["Http L01.01.11"]
        Amqp["Amqp L01.01.01"]
        Content["Content L01.01.05"]
        Identity["IdentityModel L01.01.12"]
        OTel["OpenTelemetry L01.01.16"]
        OpenApi["OpenApi L01.01.15"]
    end

    subgraph W4["Wave 4: Composition & High-Risk"]
        Hosting["Hosting L01.01.10"]
        AppModel["ApplicationModel L01.01.02"]
        Dns["Dns L01.01.08"]
    end

    Core --> DI
    Core --> Config
    Core --> Logging
    Core --> FS
    Core --> Net
    Core --> Cache
    Core --> Resilience
    Core --> Http
    Core --> Amqp
    Core --> Dns
    Core --> Hosting
    Core --> OTel
    Core --> OpenApi
    Security --> Identity
    FS --> Config
    FS --> Content
    Net --> Http
    Net --> Amqp
    DI --> Hosting
    DI --> AppModel
    Config --> AppModel
    Logging --> AppModel
    FS --> AppModel
    Hosting --> AppModel

```