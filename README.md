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
![SDK](https://github.com/assimalign/cohesion/actions/workflows/sdk.yml/badge.svg?branch=development)

Cohesion is a mono repository 

## Libraries
![amqp](https://github.com/assimalign/cohesion/actions/workflows/library-amqp.yml/badge.svg?branch=development)
![application model](https://github.com/assimalign/cohesion/actions/workflows/library-application-model.yml/badge.svg?branch=development)
![configuration](https://github.com/assimalign/cohesion/actions/workflows/library-configuration.yml/badge.svg?branch=development)
![content](https://github.com/assimalign/cohesion/actions/workflows/library-content.yml/badge.svg?branch=development)
![core](https://github.com/assimalign/cohesion/actions/workflows/library-core.yml/badge.svg?branch=development)
![dns](https://github.com/assimalign/cohesion/actions/workflows/library-dns.yml/badge.svg?branch=development)
![filesystem](https://github.com/assimalign/cohesion/actions/workflows/library-filesystem.yml/badge.svg?branch=development)
![hosting](https://github.com/assimalign/cohesion/actions/workflows/library-hosting.yml/badge.svg?branch=development)
![http](https://github.com/assimalign/cohesion/actions/workflows/library-http.yml/badge.svg?branch=development)
![identity model](https://github.com/assimalign/cohesion/actions/workflows/library-identity-model.yml/badge.svg?branch=development)
![logging](https://github.com/assimalign/cohesion/actions/workflows/library-logging.yml/badge.svg?branch=development)
![openapi](https://github.com/assimalign/cohesion/actions/workflows/library-openapi.yml/badge.svg?branch=development)
![opentelemetry](https://github.com/assimalign/cohesion/actions/workflows/library-opentelemetry.yml/badge.svg?branch=development)
![resilience](https://github.com/assimalign/cohesion/actions/workflows/library-resilience.yml/badge.svg?branch=development)



## Services/Resources
![api manager](https://github.com/assimalign/cohesion/actions/workflows/resource-api-manager.yml/badge.svg?branch=development)
![configuration store](https://github.com/assimalign/cohesion/actions/workflows/resource-configuration-store.yml/badge.svg?branch=development)
![database](https://github.com/assimalign/cohesion/actions/workflows/resource-database.yml/badge.svg?branch=development)
![event hub](https://github.com/assimalign/cohesion/actions/workflows/resource-event-hub.yml/badge.svg?branch=development)
![iot hub](https://github.com/assimalign/cohesion/actions/workflows/resource-iot-hub.yml/badge.svg?branch=development)
![load balancer](https://github.com/assimalign/cohesion/actions/workflows/resource-load-balancer.yml/badge.svg?branch=development)
![log space](https://github.com/assimalign/cohesion/actions/workflows/resource-log-space.yml/badge.svg?branch=development)
![media hub](https://github.com/assimalign/cohesion/actions/workflows/resource-media-hub.yml/badge.svg?branch=development)
![message hub](https://github.com/assimalign/cohesion/actions/workflows/resource-message-hub.yml/badge.svg?branch=development)
![nat gateway](https://github.com/assimalign/cohesion/actions/workflows/resource-nat-gateway.yml/badge.svg?branch=development)
![notification hub](https://github.com/assimalign/cohesion/actions/workflows/resource-notification-hub.yml/badge.svg?branch=development)
![secret store](https://github.com/assimalign/cohesion/actions/workflows/resource-secret-store.yml/badge.svg?branch=development)
![web](https://github.com/assimalign/cohesion/actions/workflows/resource-web.yml/badge.svg?branch=development)

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

