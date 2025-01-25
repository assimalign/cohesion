# Cohesion

Cohesion is 

- [Cohesion](#cohesion)
- [Sdk](#sdk)
  - [Services/Resources](#servicesresources)
  - [Tooling](#tooling)
  - [Extensions](#extensions)
- [Repository Structure](#repository-structure)

# Sdk
![SDK](https://github.com/assimalign/cohesion/actions/workflows/sdk.yml/badge.svg?branch=development)

Cohesion is a mono repository 


## Services/Resources

![Core](https://github.com/assimalign/cohesion/actions/workflows/core.yml/badge.svg?branch=development) 
![Configuration Store](https://github.com/assimalign/cohesion/actions/workflows/configuration-store.yml/badge.svg?branch=development)
![API Manager](https://github.com/assimalign/cohesion/actions/workflows/api-manager.yml/badge.svg?branch=development)
![Database](https://github.com/assimalign/cohesion/actions/workflows/database.yml/badge.svg?branch=development)
![Dns](https://github.com/assimalign/cohesion/actions/workflows/dns.yml/badge.svg?branch=development)
![Event Hub](https://github.com/assimalign/cohesion/actions/workflows/event-hub.yml/badge.svg?branch=development)
![Identity](https://github.com/assimalign/cohesion/actions/workflows/identity.yml/badge.svg?branch=development)
![IoT Hub](https://github.com/assimalign/cohesion/actions/workflows/iot-hub.yml/badge.svg?branch=development)
![Load Balancer](https://github.com/assimalign/cohesion/actions/workflows/load-balancer.yml/badge.svg?branch=development)
![Log Space](https://github.com/assimalign/cohesion/actions/workflows/log-space.yml/badge.svg?branch=development)
![Media Hub](https://github.com/assimalign/cohesion/actions/workflows/media-hub.yml/badge.svg?branch=development)
![Message Hub](https://github.com/assimalign/cohesion/actions/workflows/message-hub.yml/badge.svg?branch=development)
![NAT Gateway](https://github.com/assimalign/cohesion/actions/workflows/nat-gateway.yml/badge.svg?branch=development)
![Notification Hub](https://github.com/assimalign/cohesion/actions/workflows/notification-hub.yml/badge.svg?branch=development)
![OpenTelemetry](https://github.com/assimalign/cohesion/actions/workflows/opentelemetry.yml/badge.svg?branch=development)
![Secret Store](https://github.com/assimalign/cohesion/actions/workflows/secret-store.yml/badge.svg?branch=development)
![Web](https://github.com/assimalign/cohesion/actions/workflows/web.yml/badge.svg?branch=development)

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

