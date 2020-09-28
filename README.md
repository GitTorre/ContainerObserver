# ContainerObserver

This is a sample impl of a [FabricObserver](https://aka.ms/sf/fabricobserver) plugin that monitors dockerd process hosted Service Fabric app container instances for CPU and Memory use. It is incomplete and
requires testing. The purpose of this is to show how you could design and implement a FabricObserver plugin to monitor container resource use for containers that are hosted in a dockerd process
in a Windows Service Fabric Cluster. Feel free to modify and submit a pull request if you get this working with changes (and fixes to the design/impl).
